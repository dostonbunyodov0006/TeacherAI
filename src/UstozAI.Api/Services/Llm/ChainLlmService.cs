using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;

namespace UstozAI.Api.Services.Llm;

/// <summary>A named link of the chain.</summary>
public record LlmLink(string Name, ILlmService Service, bool TrainsOnFreeTier = false, bool IsFallback = false);

/// <summary>
/// "Miyalar zanjiri": Claude → Gemini (free) → Groq (free) → offline lines. A provider that fails BEFORE its first
/// token (rate limit, daily quota, no credit, outage) is skipped and rests for a while; the student never notices.
/// Students under 16 get providers whose free tier trains on conversations last.
/// </summary>
public class ChainLlmService(IReadOnlyList<LlmLink> links, ILogger<ChainLlmService> log, TimeProvider? clock = null) : ILlmService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _restUntil = new();

    public IReadOnlyList<LlmLink> Links => links;
    public string Describe() => string.Join("+", links.Where(l => !l.IsFallback).Select(l => l.Name)) is { Length: > 0 } s ? s : "offline";

    /// <summary>The order for this student: the configured order, but "trains on free tier" links last for minors.</summary>
    public IEnumerable<LlmLink> OrderFor(LlmContext ctx) =>
        ctx.Minor ? links.OrderBy(l => l.IsFallback ? 2 : l.TrainsOnFreeTier ? 1 : 0) : links;

    public bool IsResting(string name) => _restUntil.TryGetValue(name, out var t) && t > _clock.GetUtcNow();

    public async IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatTurn> history, LlmContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var link in OrderFor(context))
        {
            if (!link.IsFallback && IsResting(link.Name)) continue;
            IAsyncEnumerator<string>? e = null;
            string? first = null;
            try
            {
                e = link.Service.StreamReplyAsync(history, context, ct).GetAsyncEnumerator(ct);
                if (await e.MoveNextAsync()) first = e.Current;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var rest = RestFor(ex);
                if (rest > TimeSpan.Zero) _restUntil[link.Name] = _clock.GetUtcNow() + rest;
                log.LogWarning("LLM {Name} failed before its first token ({Error}); resting {Rest}, trying the next one",
                    link.Name, Short(ex), rest);
            }
            if (first is null)                                   // failed or said nothing → next link
            {
                if (e is not null) await e.DisposeAsync();
                continue;
            }
            if (!link.IsFallback) log.LogInformation("LLM reply from {Name}", link.Name);
            try
            {
                yield return first;
                while (await e!.MoveNextAsync()) yield return e.Current;
            }
            finally { await e!.DisposeAsync(); }
            yield break;
        }
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
        if (Has("credit balance", "billing", "insufficient_quota")) return TimeSpan.FromHours(1);          // no money
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || Has("authentication_error", "invalid x-api-key", "API_KEY_INVALID"))
            return TimeSpan.FromHours(6);                                                              // bad / revoked key
        if (status == HttpStatusCode.TooManyRequests || Has("rate_limit", "RESOURCE_EXHAUSTED", "429"))
            return Has("per day", "PerDay", "daily", "RPD", "tokens per day") ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(60);
        if (status is >= HttpStatusCode.InternalServerError || Has("overloaded", "UNAVAILABLE") || ex is TaskCanceledException or TimeoutException)
            return TimeSpan.FromSeconds(30);                                                           // outage / slow
        return TimeSpan.Zero;                                                                          // e.g. one bad request
    }

    private static string Short(Exception ex) => ex.Message.Length > 160 ? ex.Message[..160] : ex.Message;
}
