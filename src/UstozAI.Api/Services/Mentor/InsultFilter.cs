using System.Text.RegularExpressions;

namespace UstozAI.Api.Services.Mentor;

/// <summary>
/// Server-side guarantee for the raw vocabulary: the model is *told* the rules (MentorPersona.StyleVocabulary),
/// this makes sure they hold. Runs on every speech segment BEFORE TTS, so a masked word is never spoken.
///   soft            → all insult words masked
///   strict          → only the mild forms ("jinnimisan", "kasalmisan") pass, at rage ≥ 7
///   savage (opt-in) → tier-1 words at rage ≥ 6, tier-2 (vulgar) at rage ≥ 8
///   empathy mode    → everything masked, regardless of style (the student is hurt/stressed)
/// Ordinary words like "kasal" in "kasal bo'lsang shifokorga bor" are untouched — only insult forms are listed.
/// </summary>
public static partial class InsultFilter
{
    public static string Apply(string text, string style, bool empathy, int rage, out int masked)
    {
        var count = 0;
        string Mask(Match m) { count++; return "…"; }

        var allowMild = !empathy && ((style == "strict" && rage >= 7) || (style == "savage" && rage >= 6));
        var allowTier1 = !empathy && style == "savage" && rage >= 6;
        var allowTier2 = !empathy && style == "savage" && rage >= 8;

        if (!allowTier2) text = Tier2().Replace(text, Mask);
        if (!allowTier1) text = Tier1().Replace(text, Mask);
        if (!allowMild) text = Mild().Replace(text, Mask);
        if (empathy || style == "soft") text = Playful().Replace(text, Mask);   // "Eh, dalbay!" — teasing, not abuse
        masked = count;
        return count == 0 ? text : Spaces().Replace(text, " ").Replace(" …", "…").Trim();
    }

    // vulgar (incl. Cyrillic spellings)
    [GeneratedRegex(@"\b(dalba?yob\w*|dalban\w*|долбо[её]б\w*|далбаёб\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex Tier2();

    [GeneratedRegex(@"\b(ahmoq\w*|tupoy\w*|durak\w*|тупо[йя]\w*|дура[кч]\w*|jinni\b|kasal\b(?!\s+bo'l))", RegexOptions.IgnoreCase)]
    private static partial Regex Tier1();

    [GeneratedRegex(@"\b(jinnimisan|kasalmisan|jinni\s+bo'ldingmi)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Mild();

    [GeneratedRegex(@"\b(dalbay(?!ob)\b|балбес\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex Playful();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
