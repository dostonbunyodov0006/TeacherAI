using UstozAI.Api.Services.Mentor;
using UstozAI.Api.Services.Pipeline;

namespace UstozAI.Tests;

public class UtteranceGuardTests
{
    [Theory]
    [InlineData("ustoz...", CutoffReason.Filler)]               // only filler words
    [InlineData("Ustoz, haligi", CutoffReason.Filler)]
    [InlineData("nima edi", CutoffReason.Filler)]
    [InlineData("ну это", CutoffReason.Filler)]
    [InlineData("Present perfect qachon…", CutoffReason.TrailingOff)]
    [InlineData("Men bugun keldim va", CutoffReason.TrailingOff)]   // ends on a conjunction
    [InlineData("", CutoffReason.Unclear)]
    public void Detects_fragments(string text, CutoffReason expected) =>
        Assert.Equal(expected, UtteranceGuard.Check(text, 0.9, 2000, 1500, clippedEnd: false));

    [Theory]
    [InlineData("Present Perfect qachon ishlatiladi?")]
    [InlineData("Ustoz, IELTS ga qanday tayyorlansam bo'ladi?")]
    [InlineData("Men bugun uy vazifasini qildim")]
    [InlineData("Haligi kitobni o'qib chiqdim, juda qiziq ekan")]   // a filler at the START is normal speech
    [InlineData("Avval grammatikani, keyin lug'atni o'rganaman deb o'ylayman, lekin vaqtim kam...")] // long thought + …
    public void Lets_complete_sentences_through(string text) =>
        Assert.Null(UtteranceGuard.Check(text, 0.9, 2500, 2000, clippedEnd: false));

    [Fact]
    public void Client_stats_win_before_text()
    {
        Assert.Equal(CutoffReason.TooShort, UtteranceGuard.Check("Salom ustoz qalaysiz", 0.9, 500, 400, false));
        Assert.Equal(CutoffReason.Silent, UtteranceGuard.Check("Salom ustoz qalaysiz", 0.9, 3000, 100, false));
    }

    [Fact]
    public void Clipped_end_makes_a_dangling_long_sentence_a_cutoff()
    {
        const string s = "Men ertaga imtihonga tayyorlanmoqchiman lekin";
        Assert.Equal(CutoffReason.TrailingOff, UtteranceGuard.Check(s, 0.9, 3000, 2500, clippedEnd: true));
        Assert.Equal(CutoffReason.ClippedMidWord, UtteranceGuard.Check("Present perf", 0.9, 1500, 1200, clippedEnd: true));
    }

    [Fact]
    public void Low_confidence_short_is_unclear_but_long_is_trusted()
    {
        Assert.Equal(CutoffReason.Unclear, UtteranceGuard.Check("bla bla", 0.2, 1500, 1200, false));
        Assert.Null(UtteranceGuard.Check("Bugun men grammatika mashqlarini bajardim", 0.2, 3000, 2600, false));
    }
}

public class InsultFilterTests
{
    private const string Line = "Shu gapni aytgan odam ahmoq-da, dalbayob! Jinnimisan?";

    [Fact]
    public void Soft_masks_everything()
    {
        var r = InsultFilter.Apply(Line, "soft", empathy: false, rage: 10, out var n);
        Assert.Equal(3, n);
        Assert.DoesNotContain("ahmoq", r); Assert.DoesNotContain("dalbayob", r); Assert.DoesNotContain("Jinnimisan", r);
    }

    [Fact]
    public void Strict_allows_only_mild_forms_at_high_rage()
    {
        var r = InsultFilter.Apply(Line, "strict", false, 8, out _);
        Assert.Contains("Jinnimisan", r);
        Assert.DoesNotContain("ahmoq", r); Assert.DoesNotContain("dalbayob", r);
        Assert.DoesNotContain("Jinnimisan", InsultFilter.Apply(Line, "strict", false, 5, out _));
    }

    [Theory]
    [InlineData(5, false, false)]
    [InlineData(6, true, false)]
    [InlineData(8, true, true)]
    public void Savage_tiers_follow_rage(int rage, bool tier1, bool tier2)
    {
        var r = InsultFilter.Apply(Line, "savage", false, rage, out _);
        Assert.Equal(tier1, r.Contains("ahmoq"));
        Assert.Equal(tier2, r.Contains("dalbayob"));
    }

    [Fact]
    public void Empathy_masks_everything_even_in_savage()
    {
        var r = InsultFilter.Apply(Line, "savage", empathy: true, rage: 10, out var n);
        Assert.Equal(3, n);
    }

    [Theory]
    [InlineData("Kasal bo'lsang shifokorga bor.")]
    [InlineData("Bu kitob juda qiziq, durust yozilgan.")]
    public void Ordinary_words_are_untouched(string text)
    {
        Assert.Equal(text, InsultFilter.Apply(text, "soft", true, 0, out var n));
        Assert.Equal(0, n);
    }
}
