using System.Text.RegularExpressions;

namespace UstozAI.Api.Services.Speech;

/// <summary>
/// The three conversation languages. The student may speak any of them; Ustoz answers in the same one,
/// with a native neural voice per language.
/// </summary>
public static partial class LanguageSupport
{
    public const string Uzbek = "uz-UZ", Russian = "ru-RU", English = "en-US";
    public static readonly string[] All = [Uzbek, Russian, English];

    /// <summary>Default male mentor voice per language (Azure neural).</summary>
    public static string VoiceFor(string lang) => Normalize(lang) switch
    {
        Russian => "ru-RU-DmitryNeural",
        English => "en-US-AndrewNeural",
        _ => "uz-UZ-SardorNeural",
    };

    public static string Normalize(string? lang) => (lang ?? "").ToLowerInvariant() switch
    {
        var l when l.StartsWith("ru") => Russian,
        var l when l.StartsWith("en") => English,
        var l when l.StartsWith("uz") => Uzbek,
        _ => Uzbek,
    };

    public static bool IsSupported(string? lang) => lang is not null && All.Contains(lang);

    public static string NameUz(string lang) => Normalize(lang) switch
    {
        Russian => "rus", English => "ingliz", _ => "o'zbek",
    };

    /// <summary>
    /// Cheap text language guess for typed messages (and as a fallback when STT gives no language).
    /// Cyrillic → Russian unless it has Uzbek-only Cyrillic letters (ў қ ғ ҳ); Latin → Uzbek when it carries
    /// Uzbek orthography (o' g' sh/ch + q/x) or common Uzbek words, otherwise English. Null when undecidable.
    /// </summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Teacher's NAME says nothing about the language ("Teacher, choy iching" is Uzbek) — drop it first.
        var t = NameWords().Replace(text.ToLowerInvariant(), " ");
        int cyr = 0, lat = 0;
        foreach (var ch in t)
        {
            if (ch is >= 'а' and <= 'я' or 'ё' or 'ў' or 'қ' or 'ғ' or 'ҳ') cyr++;
            else if (ch is >= 'a' and <= 'z') lat++;
        }
        if (cyr + lat < 2) return null;
        if (cyr > lat) return UzCyrillic().IsMatch(t) ? Uzbek : Russian;

        var uzScore = UzMarks().Matches(t).Count * 2 + UzWords().Matches(t).Count * 3;
        var enScore = EnWords().Matches(t).Count * 3;
        if (uzScore == 0 && enScore == 0) return null;
        return uzScore >= enScore ? Uzbek : English;
    }

    [GeneratedRegex("[ўқғҳ]")] private static partial Regex UzCyrillic();
    [GeneratedRegex(@"\b(teacher|ticher|tichir|тичер|тичэр|учитель|ustoz|устоз)\b")] private static partial Regex NameWords();
    // o'/g' apostrophe letters + typical Uzbek suffixes (-lar, -ning, -ga, -dan, -yapman, -moqda …)
    [GeneratedRegex(@"[og]['‘’ʻ`](?=\w)|\b\w{2,}(lar|larni|larga|ning|dagi|ga|dan|da|ni|mi|dim|dik|man|san|yapman|yapti|moqda|moqchi|ish|ishni)\b")]
    private static partial Regex UzMarks();
    [GeneratedRegex(@"\b(men|sen|siz|biz|nima|qanday|qachon|nega|uchun|bilan|ham|emas|bor|yo'q|yaxshi|ustoz|kerak|mumkin|bu|shu|va|lekin|chunki|qilib|qildim|bo'ladi|bo'lsa|salom|rahmat|iltimos|qara|qarang|qarab|choy|ich|iching|yot|yoting|tur|turing|o'tir|o'tiring|otiring|hazillashdim|tepaga|pastga|chapga|o'ngga|onga|menga|keling|qani|xo'p|mayli|ha|yo'q|endi|darsni|boshlaymiz)\b")]
    private static partial Regex UzWords();
    [GeneratedRegex(@"\b(the|is|are|what|how|why|when|you|i|my|me|do|does|can|could|would|should|please|thanks|hello|and|but|because|this|that|with|have|has|look|left|right|up|down|sit|stand|lie|get|drink|tea|some|kidding|joking|let's|start|lesson)\b")]
    private static partial Regex EnWords();
}
