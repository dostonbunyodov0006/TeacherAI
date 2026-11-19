namespace UstozAI.Api.Services;

/// <summary>Speech-to-Text. Input is 16 kHz mono 16-bit PCM WAV (encoded in the browser).</summary>
public interface ISpeechToText
{
    /// <param name="language">A locale, or "auto" → detect among uz-UZ / ru-RU / en-US.</param>
    Task<SttResult> TranscribeAsync(byte[] wav, string language, CancellationToken ct);
}
/// <param name="Language">Detected spoken language (uz-UZ / ru-RU / en-US) when auto-detection was used.</param>
public record SttResult(string Text, double? Confidence, IReadOnlyList<PronouncedWord>? Words = null, string? Language = null);
/// <summary>Per-word pronunciation accuracy 0..100 (Azure pronunciation assessment; English only).</summary>
public record PronouncedWord(string Word, double Accuracy);

/// <param name="Images">Images attached to this (user) turn, sent to the vision model. Null for text-only turns.</param>
/// <param name="MessageId">DB id — lets the pipeline attach images to the right history turns.</param>
public record ChatTurn(bool IsUser, string Text, IReadOnlyList<ImageInput>? Images = null, long MessageId = 0);
public record ImageInput(string MediaType, string Base64);

/// <param name="SessionDate">The day this thread belongs to (may be in the past when a user continues an old day).</param>
/// <param name="Today">The user's local date now.</param>
/// <param name="MentorBrief">Per-turn facts: real level, pending homework, current anger (from MentorStateService).</param>
/// <param name="Style">"soft" | "strict"</param>
public record LlmContext(string Language, DateOnly SessionDate, DateOnly Today, string MentorBrief = "", string Style = "strict");

/// <summary>Streams the mentor's reply as raw JSON text deltas (schema: Services/Mentor/MentorReply.cs).</summary>
public interface ILlmService
{
    IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatTurn> history, LlmContext context, CancellationToken ct);
}

/// <summary>One synthesized sentence: audio plus mouth-shape timeline.</summary>
public record SpeechChunk(string? AudioBase64, string AudioMime, int DurationMs, IReadOnlyList<VisemeCue> Visemes);

/// <summary>Viseme cue. <see cref="Shape"/> is one of the client's mouth shapes: rest, A, E, I, O, U, MBP, FV, L.</summary>
public record VisemeCue(int OffsetMs, string Shape);

public interface ITextToSpeech
{
    /// <param name="emotion">Segment emotion → prosody (rate/pitch/volume) so anger or sarcasm is audible.</param>
    Task<SpeechChunk> SynthesizeAsync(string text, string language, string voice, string emotion, CancellationToken ct);
}
