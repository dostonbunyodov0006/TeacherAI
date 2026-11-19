using Microsoft.Extensions.Caching.Memory;

namespace UstozAI.Api.Services.Speech;

/// <summary>
/// Decorator: short lines that recur (cut-off snaps, "Barakalla!", quiz prompts…) are synthesized once and then
/// served from memory — zero TTS latency on repeats. Long, one-off sentences bypass the cache.
/// </summary>
public class CachedTextToSpeech(ITextToSpeech inner, IMemoryCache cache) : ITextToSpeech
{
    private const int MaxCachedChars = 140;

    public Task<SpeechChunk> SynthesizeAsync(string text, string language, string voice, string emotion, CancellationToken ct)
    {
        if (text.Length > MaxCachedChars) return inner.SynthesizeAsync(text, language, voice, emotion, ct);
        var key = $"tts|{voice}|{language}|{emotion}|{text}";
        return cache.GetOrCreateAsync(key, e =>
        {
            e.SlidingExpiration = TimeSpan.FromHours(6);
            e.Size = Math.Max(1, text.Length);
            return inner.SynthesizeAsync(text, language, voice, emotion, ct);
        })!;
    }
}
