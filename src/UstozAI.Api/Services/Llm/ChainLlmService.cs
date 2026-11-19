using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;

namespace UstozAI.Api.Services.Llm;

/// <summary>A named link of the chain.</summary>
public record LlmLink(string Name, ILlmService Service, bool TrainsOnFreeTier = false, bool IsFallback = false);

/// <summary>
/// "Miyalar zanjiri": Claude → Gemini (free) → Groq (free) → offline lines.
/// • A brain that fails BEFORE its first token (rate limit, quota, no credit, outage) is skipped and rests a while.
/// • Hedging: if the current brain hasn't started answering within <see cref="HedgeAfter"/>, the next one is started
///   IN PARALLEL and whichever speaks first wins (the other is cancelled) — a slow or overloaded free server can no
///   longer keep the student waiting.
/// • Students under 16 get providers that train on free-tier chats last.
/// </summary>
public class ChainLlmService(IReadOnlyList<LlmLink> links, ILogger<ChainLlmService> log, TimeProvider? clock = null) : ILlmService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _restUntil = new();

    /// <summary>How long to wait for the first token before also asking the next brain.</summary>
    public TimeSpan HedgeAfter { get; init; } = TimeSpan.FromSeconds(2);

    public IReadOnlyList<LlmLink> Links => links;
    public string Describe() => string.Join("+", links.Where(l => !l.IsFallback).Select(l => l.Name)) is { Length: > 0 } s ? s : "offline";

    /// <summary>The order for this student: the configured order, but "trains on free tier" links last for minors.</summary>
    public IEnumerable<LlmLink> OrderFor(LlmContext ctx) =>
        ctx.Minor ? links.OrderBy(l => l.IsFallback ? 2 : l.TrainsOnFreeTier ? 1 : 0) : links;

    public bool IsResting(string name) => _restUntil.TryGetValue(name, out var t) && t > _clock.GetUtcNow();

    private sealed record Attempt(LlmLink Link, CancellationTokenSource Cts, Stopwatch Clock, Task<(IAsyncEnumerator<string> E, string First)?> Started);

    public async IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatTurn> history, LlmContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ordered = OrderFor(context).ToList();
        var queue = new Queue<LlmLink>(ordered.Where(l => !l.IsFallback && !IsResting(l.Name)));
        var fallback = ordered.FirstOrDefault(l => l.IsFallback);
        var running = new List<Attempt>();
        Attempt? winner = null;
        (IAsyncEnumerator<string> E, string First) won = default;

        Attempt Launch(LlmLink link)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            return new Attempt(link, cts, Stopwatch.StartNew(), FirstTokenAsync(link, history, context, cts.Token));
        }

        if (queue.Count > 0) running.Add(Launch(queue.Dequeue()));
        while (winner is null && running.Count > 0)
        {
            var waits = running.Select(a => (Task)a.Started).ToList();
            Task? hedge = null;
            if (queue.Count > 0) waits.Add(hedge = Task.Delay(HedgeAfter, ct));
            var done = await Task.WhenAny(waits);
            ct.ThrowIfCancellationRequested();
            if (done == hedge)                                       // too slow → ask the next brain as well
            {
                var next = queue.Dequeue();
                log.LogInformation("LLM {Slow} hasn't started after {Ms} ms — also asking {Next}",
                    string.Join(",", running.Select(a => a.Link.Name)), (int)HedgeAfter.TotalMilliseconds, next.Name);
                running.Add(Launch(next));
                continue;
            }
            var finished = running.First(a => a.Started == done);
            running.Remove(finished);
            if (finished.Started.Result is { } r) { winner = finished; won = r; }
            else if (running.Count == 0 && queue.Count > 0) running.Add(Launch(queue.Dequeue()));   // failed → next
        }
        foreach (var loser in running) Abandon(loser);                // the slower ones are cancelled

        if (winner is null)                                          // every brain is down → offline lines
        {
            if (fallback is null) yield break;
            await foreach (var p in fallback.Service.StreamReplyAsync(history, context, ct)) yield return p;
            yield break;
        }

        var firstMs = winner.Clock.ElapsedMilliseconds;
        var chunks = 1;
        try
        {
            yield return won.First;
            while (await won.E.MoveNextAsync()) { chunks++; yield return won.E.Current; }
        }
        finally
        {
            await won.E.DisposeAsync();
            winner.Cts.Dispose();
            log.LogInformation("LLM reply from {Name}: first token {First} ms, total {Total} ms, {Chunks} chunks",
                winner.Link.Name, firstMs, winner.Clock.ElapsedMilliseconds, chunks);
        }
    }

    /// <summary>Starts one brain and waits for its first token. Null = it failed (and now rests) or said nothing.</summary>
    private async Task<(IAsyncEnumerator<string> E, string First)?> FirstTokenAsync(LlmLink link, IReadOnlyList<ChatTurn> history,
        LlmContext context, CancellationToken ct)
    {
        IAsyncEnumerator<string>? e = null;
        try
        {
            e = link.Service.StreamReplyAsync(history, context, ct).GetAsyncEnumerator(ct);
            if (await e.MoveNextAsync()) return (e, e.Current);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var rest = RestFor(ex);
            if (rest > TimeSpan.Zero) _restUntil[link.Name] = _clock.GetUtcNow() + rest;
            log.LogWarning("LLM {Name} failed before its first token ({Error}); resting {Rest}", link.Name, Short(ex), rest);
        }
        catch (OperationCanceledException) { /* lost the race */ }
        if (e is not null) await e.DisposeAsync();
        return null;
    }

    /// <summary>Cancel a losing attempt and release it in the background.</summary>
    private static void Abandon(Attempt a)
    {
        a.Cts.Cancel();
        _ = a.Started.ContinueWith(async t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } r) await r.E.DisposeAsync();
            a.Cts.Dispose();
        }, TaskScheduler.Default);
    }

    /// <summary>How long a failing provider should rest before it is tried again.</summary>
    public static TimeSpan RestFor(Exception ex)
    {
        var status = ex switch
        {
            LlmProviderException p => p.Status,
            HttpRequestException h => h.StatusCode,
            _ => null,
        };
        var text = ex.ToString();
        bool Has(params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        // Rate limits first: their messages often mention "billing"/"upgrade", which is NOT a missing credit.
        if (status == HttpStatusCode.TooManyRequests || Has("rate_limit", "rate limit", "RESOURCE_EXHAUSTED"))
            return Has("per day", "PerDay", "daily", "RPD", "TPD", "tokens per day", "requests per day")
                ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(60);
        if (Has("credit balance", "insufficient_quota") || status == HttpStatusCode.PaymentRequired) return TimeSpan.FromHours(1);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || Has("authentication_error", "invalid x-api-key", "API_KEY_INVALID"))
            return TimeSpan.FromHours(6);                                                              // bad / revoked key
        if (status is >= HttpStatusCode.InternalServerError || Has("overloaded", "UNAVAILABLE") || ex is TaskCanceledException or TimeoutException)
            return TimeSpan.FromSeconds(30);                                                           // outage / slow
        return TimeSpan.Zero;                                                                          // e.g. one bad request
    }

    private static string Short(Exception ex) => ex.Message.Length > 160 ? ex.Message[..160] : ex.Message;
}
