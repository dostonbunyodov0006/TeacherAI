using Microsoft.Extensions.Caching.Memory;
using UstozAI.Api.Services;
using UstozAI.Api.Services.Speech;

namespace UstozAI.Tests;

public class CachedTtsTests
{
    private sealed class FlakyTts : ITextToSpeech
    {
        public int Calls;
        public Task<SpeechChunk> SynthesizeAsync(string text, string language, string voice, string emotion, CancellationToken ct)
        {
            Calls++;
            if (Calls == 1) throw new OperationCanceledException(ct);       // first caller was barged-in
            return Task.FromResult(new SpeechChunk("AAAA", "audio/mpeg", 100, []));
        }
    }

    [Fact]
    public async Task A_cancelled_first_synthesis_is_not_served_to_later_turns()
    {
        var inner = new FlakyTts();
        var tts = new CachedTextToSpeech(inner, new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tts.SynthesizeAsync("Hmph.", "uz-UZ", "v", "neutral", default));
        var second = await tts.SynthesizeAsync("Hmph.", "uz-UZ", "v", "neutral", default);
        Assert.Equal("AAAA", second.AudioBase64);
        Assert.Equal(2, inner.Calls);
    }
}
