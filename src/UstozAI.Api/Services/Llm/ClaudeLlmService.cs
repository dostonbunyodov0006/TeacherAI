using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;
using UstozAI.Api.Services.Mentor;

namespace UstozAI.Api.Services.Llm;

public class LlmOptions
{
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>low = fastest first token; good for spoken tutoring. Raise to medium/high for harder subjects.</summary>
    public string Effort { get; set; } = "low";
    public int MaxTokens { get; set; } = 4096;
    public int HistoryTurns { get; set; } = 30;
}

/// <summary>
/// Claude-backed mentor. Returns the structured reply as streamed JSON text (structured outputs enforce the
/// schema), so <see cref="StreamingReplyParser"/> can hand segment 1 to TTS before the reply is finished.
/// </summary>
public class ClaudeLlmService(AnthropicClient client, IOptions<LlmOptions> options, ILogger<ClaudeLlmService> log) : ILlmService
{
    private readonly LlmOptions _o = options.Value;
    private static readonly Dictionary<string, System.Text.Json.JsonElement> Schema = MentorJson.Schema();

    public async IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatTurn> history, LlmContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<BetaMessageParam>();
        foreach (var t in NormalizeHistory(history, _o.HistoryTurns))
            messages.Add(t.IsUser ? UserMessage(t) : new BetaAssistantMessageParam { Content = t.Text });

        var parameters = new MessageCreateParams
        {
            Model = _o.Model,
            MaxTokens = _o.MaxTokens,
            System = new List<BetaTextBlockParam>
            {
                // Static persona (same bytes every turn) → cached prefix.
                new() { Text = MentorPersona.System(context.Language, context.Style), CacheControl = new BetaCacheControlEphemeral() },
                // Per-turn facts: real level, homework, anger, continuity.
                new() { Text = context.MentorBrief + MentorPersona.Continuity(context.SessionDate, context.Today) },
            },
            OutputConfig = new BetaOutputConfig
            {
                Effort = ParseEffort(_o.Effort),
                Format = new BetaJsonOutputFormat { Schema = Schema },
            },
            Messages = messages,
            // Server-side refusal fallback: if a safety classifier declines, the API retries on a suitable model.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
        };

        await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return text.Text;
            else if (ev.TryPickDelta(out var md) && md.Delta.StopReason is { } sr && sr == BetaStopReason.Refusal)
                log.LogWarning("Claude refused a request");   // pipeline falls back to a neutral reply
        }
    }

    /// <summary>Vision: images first, then the text — Claude reads image→question best in that order.</summary>
    /// <summary>
    /// Makes any stored history a valid conversation for the API: the window always STARTS with the student
    /// (a leading Teacher line — e.g. a weekly roast or a cut window — is dropped), roles strictly ALTERNATE
    /// (two Teacher lines in a row are joined; a student line whose reply was never saved, e.g. a failed turn,
    /// gets a short placeholder reply) and it ENDS with the student's current message.
    /// </summary>
    public static List<ChatTurn> NormalizeHistory(IReadOnlyList<ChatTurn> history, int max)
    {
        var window = history.TakeLast(Math.Max(1, max)).SkipWhile(t => !t.IsUser).ToList();
        var result = new List<ChatTurn>(window.Count + 4);
        foreach (var t in window)
        {
            if (result.Count > 0 && result[^1].IsUser == t.IsUser)
            {
                if (t.IsUser) result.Add(new ChatTurn(false, "(…)"));                   // the reply to that turn was lost
                else { result[^1] = result[^1] with { Text = $"{result[^1].Text} {t.Text}".Trim() }; continue; }
            }
            result.Add(t);
        }
        while (result.Count > 0 && !result[^1].IsUser) result.RemoveAt(result.Count - 1);  // must end with the student
        return result;
    }

    private static BetaMessageParam UserMessage(ChatTurn t)
    {
        if (t.Images is not { Count: > 0 }) return new BetaUserMessageParam { Content = t.Text };
        var blocks = new List<BetaContentBlockParam>();
        foreach (var img in t.Images)
            blocks.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = img.Base64, MediaType = ToMediaType(img.MediaType) },
            });
        blocks.Add(new BetaTextBlockParam { Text = string.IsNullOrWhiteSpace(t.Text) ? "(rasm)" : t.Text });
        return new BetaUserMessageParam { Content = blocks };
    }

    private static MediaType ToMediaType(string mime) => mime switch
    {
        "image/png" => MediaType.ImagePng,
        "image/webp" => MediaType.ImageWebP,
        "image/gif" => MediaType.ImageGif,
        _ => MediaType.ImageJpeg,
    };

    private static Effort ParseEffort(string e) => e.ToLowerInvariant() switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        _ => Effort.Low,
    };
}
