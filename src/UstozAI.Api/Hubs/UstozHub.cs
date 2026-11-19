using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using UstozAI.Api.Auth;
using UstozAI.Api.Services.Learning;
using UstozAI.Api.Services.Pipeline;

namespace UstozAI.Api.Hubs;

/// <summary>
/// Real-time channel. Turn methods are server-to-client streams of <see cref="TurnEvent"/>:
/// transcript → mentor header → speech/FX chunks (+ board, quiz) → mistakes / quiz_result → done.
/// Disposing the stream on the client cancels the turn (barge-in).
/// </summary>
[Authorize]
public class UstozHub(ConversationPipeline pipeline, IServiceScopeFactory scopes, ILogger<UstozHub> log) : Hub
{
    public const int MaxAudioBytes = 2 * 1024 * 1024;   // ≈ 60 s of 16 kHz mono PCM16

    /// <summary>Voice turn (Space released). <paramref name="options"/> carries quiz id / answer delay / mic language.</summary>
    public async IAsyncEnumerable<TurnEvent> Ask(Guid? sessionId, string audioBase64, TurnOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        byte[]? wav = null;
        string? error = null;
        try
        {
            wav = Convert.FromBase64String(audioBase64);
            if (wav.Length > MaxAudioBytes) error = "Audio juda uzun (maksimum 60 soniya).";
            else if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
                error = "Noto'g'ri audio format (WAV kutilgan).";
        }
        catch (FormatException) { error = "Audio buzilgan."; }

        if (error is not null) { yield return TurnEvent.Error(error); yield break; }
        await foreach (var e in Run(sessionId, wav, null, Normal(options), ct)) yield return e;
    }

    public async IAsyncEnumerable<TurnEvent> AskText(Guid? sessionId, string text, TurnOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000)
        {
            yield return TurnEvent.Error("Matn bo'sh yoki juda uzun.");
            yield break;
        }
        await foreach (var e in Run(sessionId, null, text, Normal(options), ct)) yield return e;
    }

    /// <summary>
    /// Free path: the browser's own speech recognizer (Web Speech API) produced the transcript, so no audio is sent.
    /// Treated as a voice turn — the cut-off guard and command router still apply.
    /// </summary>
    public async IAsyncEnumerable<TurnEvent> AskTranscript(Guid? sessionId, string transcript, TurnOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (transcript.Length > 4000) { yield return TurnEvent.Error("Matn juda uzun."); yield break; }
        await foreach (var e in Run(sessionId, null, transcript, (options ?? new TurnOptions()) with { Kind = "browser_stt" }, ct))
            yield return e;
    }

    /// <summary>Countdown became visible → server starts its own clock for the quiz.</summary>
    public async Task<bool> QuizStarted(Guid quizId)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LearningService>()
            .MarkStartedAsync(Context.User!.UserId(), quizId, Context.ConnectionAborted);
    }

    /// <summary>Countdown hit zero without an answer → Ustoz reacts (strictly).</summary>
    public IAsyncEnumerable<TurnEvent> QuizTimeout(Guid sessionId, Guid quizId, CancellationToken ct) =>
        Run(sessionId, null, null, new TurnOptions(QuizId: quizId, Kind: "quiz_timeout"), ct);

    /// <summary>
    /// The browser detected a too-short / silent clip on Space release (nothing uploaded). Ustoz snaps back instantly.
    /// </summary>
    public IAsyncEnumerable<TurnEvent> Cutoff(Guid? sessionId, int durationMs, int voicedMs, CancellationToken ct) =>
        Run(sessionId, null, null, new TurnOptions(Kind: "cutoff", ClientDurationMs: Math.Max(0, durationMs),
                                                   VoicedMs: Math.Max(0, voicedMs)), ct);

    /// <summary>Ustoz reads the weekly report aloud, in persona.</summary>
    public IAsyncEnumerable<TurnEvent> WeeklyRoast(Guid? sessionId, CancellationToken ct) =>
        Run(sessionId, null, null, new TurnOptions(Kind: "weekly_report"), ct);

    // Clients can't pick server-only turn kinds through Ask/AskText.
    private static TurnOptions Normal(TurnOptions? o) => (o ?? new TurnOptions()) with { Kind = "normal" };

    private async IAsyncEnumerable<TurnEvent> Run(Guid? sessionId, byte[]? wav, string? text, TurnOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var userId = Context.User!.UserId();
        // Browser passes its IANA zone in the hub URL (?tz=…): WebSocket upgrades can't carry custom headers.
        var tz = Context.GetHttpContext()?.Request.Query["tz"].FirstOrDefault();
        await using var stream = pipeline.RunAsync(userId, sessionId, tz, wav, text, options, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            TurnEvent? ev = null;
            var failed = false;
            try
            {
                if (!await stream.MoveNextAsync()) break;
                ev = stream.Current;
            }
            catch (OperationCanceledException) { break; }   // user interrupted (pressed Space again)
            catch (Exception ex)
            {
                log.LogError(ex, "Turn failed");
                failed = true;
            }
            if (failed)
            {
                yield return TurnEvent.Error("Xatolik yuz berdi. Iltimos, qaytadan urinib ko'ring.");
                break;
            }
            yield return ev!;
        }
    }
}
