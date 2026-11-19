using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using UstozAI.Api.Services;
using UstozAI.Api.Services.Llm;
using Xunit;

namespace UstozAI.Tests;

/// <summary>"Miyalar zanjiri" — a failing brain is skipped (and rests), the student always gets an answer.</summary>
public class LlmChainTests
{
    private sealed class Fake(string name, Exception? fail = null, int delayMs = 0) : ILlmService
    {
        public int Calls;
        public bool Cancelled;
        public async IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatTurn> h, LlmContext c, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            await Task.Yield();
            if (delayMs > 0)
            {
                try { await Task.Delay(delayMs, ct); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            if (fail is not null) throw fail;
            yield return $"{{\"from\":\"{name}\"";
            yield return "}";
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly LlmContext Adult = new("uz-UZ", default, default);
    private static readonly LlmContext Kid = Adult with { Minor = true };
    private static async Task<string> Run(ChainLlmService chain, LlmContext ctx)
    {
        var s = "";
        await foreach (var p in chain.StreamReplyAsync([new ChatTurn(true, "salom")], ctx, default)) s += p;
        return s;
    }
    private static LlmProviderException Http(HttpStatusCode code, string body = "") => new("x", code, body);

    [Fact]
    public async Task First_brain_answers_when_healthy()
    {
        var chain = new ChainLlmService([new("Claude", new Fake("Claude")), new("Gemini", new Fake("Gemini"))], NullLogger<ChainLlmService>.Instance);
        Assert.Contains("Claude", await Run(chain, Adult));
    }

    [Fact]
    public async Task Rate_limited_brain_is_skipped_and_rests()
    {
        var clock = new Clock();
        var claude = new Fake("Claude", Http(HttpStatusCode.BadRequest, "Your credit balance is too low"));
        var gemini = new Fake("Gemini");
        var chain = new ChainLlmService([new("Claude", claude), new("Gemini", gemini)], NullLogger<ChainLlmService>.Instance, clock);
        Assert.Contains("Gemini", await Run(chain, Adult));
        Assert.True(chain.IsResting("Claude"));
        await Run(chain, Adult);
        Assert.Equal(1, claude.Calls);                                  // not hammered while resting
        clock.Now += TimeSpan.FromHours(2);
        await Run(chain, Adult);
        Assert.Equal(2, claude.Calls);                                  // tried again after the rest
    }

    [Fact]
    public async Task Everything_down_means_offline_lines_never_silence()
    {
        var chain = new ChainLlmService([
            new("Gemini", new Fake("Gemini", Http(HttpStatusCode.TooManyRequests, "RESOURCE_EXHAUSTED per day"))),
            new("Groq", new Fake("Groq", new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable))),
            new("Offline", new Fake("Offline"), IsFallback: true)], NullLogger<ChainLlmService>.Instance);
        Assert.Contains("Offline", await Run(chain, Adult));
    }

    [Fact]
    public void Kids_get_providers_that_train_on_free_chats_last()
    {
        var chain = new ChainLlmService([
            new("Gemini", new Fake("Gemini"), TrainsOnFreeTier: true), new("Groq", new Fake("Groq")),
            new("Offline", new Fake("Offline"), IsFallback: true)], NullLogger<ChainLlmService>.Instance);
        Assert.Equal(["Groq", "Gemini", "Offline"], chain.OrderFor(Kid).Select(l => l.Name));
        Assert.Equal(["Gemini", "Groq", "Offline"], chain.OrderFor(Adult).Select(l => l.Name));
        Assert.Equal("Gemini+Groq", chain.Describe());
    }

    [Fact]
    public async Task Slow_brain_is_raced_and_the_faster_one_speaks()
    {
        var slow = new Fake("Gemini", delayMs: 4000);
        var fast = new Fake("Groq");
        var chain = new ChainLlmService([new("Gemini", slow), new("Groq", fast)], NullLogger<ChainLlmService>.Instance)
            { HedgeAfter = TimeSpan.FromMilliseconds(150) };
        var t0 = DateTime.UtcNow;
        Assert.Contains("Groq", await Run(chain, Adult));
        Assert.True(DateTime.UtcNow - t0 < TimeSpan.FromSeconds(2));     // didn't wait for the slow one
        await Task.Delay(100);
        Assert.True(slow.Cancelled);                                      // the loser was cancelled
        Assert.False(chain.IsResting("Gemini"));                          // losing a race is not a failure
    }

    [Fact]
    public async Task Fast_first_brain_never_starts_the_second()
    {
        var gemini = new Fake("Gemini"); var groq = new Fake("Groq");
        var chain = new ChainLlmService([new("Gemini", gemini), new("Groq", groq)], NullLogger<ChainLlmService>.Instance);
        Assert.Contains("Gemini", await Run(chain, Adult));
        Assert.Equal(0, groq.Calls);                                      // no free quota wasted
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "Rate limit reached on tokens per minute (TPM). Upgrade to Dev Tier: console.groq.com/settings/billing", 60)]
    [InlineData(HttpStatusCode.TooManyRequests, "", 60)]
    [InlineData(HttpStatusCode.TooManyRequests, "Quota exceeded: requests per day", 1800)]
    [InlineData(HttpStatusCode.Unauthorized, "", 21600)]
    [InlineData(HttpStatusCode.BadRequest, "credit balance is too low", 3600)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", 30)]
    [InlineData(HttpStatusCode.BadRequest, "invalid json", 0)]
    public void Rest_time_matches_the_failure(HttpStatusCode code, string body, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ChainLlmService.RestFor(Http(code, body)));

    [Fact]
    public async Task Server_sent_events_are_read_into_text()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"emotion\\\":\"}}]}\n\n" +
                  ": keep-alive\n\n" +
                  "data: {\"choices\":[{\"delta\":{\"content\":\"\\\"teaching\\\"}\"}}]}\n\n" +
                  "data: [DONE]\n\n";
        var text = "";
        await foreach (var p in OpenAiCompatibleLlmService.ReadSse(new StringReader(sse), default)) text += p;
        Assert.Equal("{\"emotion\":\"teaching\"}", text);
    }
}
