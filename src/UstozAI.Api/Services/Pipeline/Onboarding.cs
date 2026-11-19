using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UstozAI.Api.Services.Pipeline;

/// <summary>
/// "Teacher o'zi tanishadi": registration asks only name + login + password; on the first lesson Teacher asks the
/// rest by voice — age → boy/girl (also on-screen buttons) → city — and stores it. Fully scripted (instant, works
/// offline), same v2 JSON contract as <see cref="CommandRouter"/>. The student can say "keyinroq" to skip.
/// </summary>
public static partial class Onboarding
{
    public const int Start = 0, AskAge = 1, AskGender = 2, AskCity = 3, Done = 4;

    /// <summary>What the student told us this turn (null = not understood).</summary>
    public record Facts(int? Age, string? Gender, string? City, string? Name, bool Skip);

    public record Outcome(int NextStep, string Kind, Facts Facts);

    // ---------------- parsing ----------------
    public static Facts Parse(string text, int step)
    {
        var t = Norm(text);
        var skip = Skip().IsMatch(t);
        var name = MyName().Match(t) is { Success: true } nm ? Cap(nm.Groups["n"].Value) : null;
        var gender = Boy().IsMatch(t) ? "male" : Girl().IsMatch(t) ? "female" : null;
        var age = ParseAge(t);
        string? city = null;
        if (step == AskCity && !skip) city = ParseCity(t);
        return new Facts(age, gender, city, name, skip);
    }

    private static string Norm(string s) => s.Trim().Replace('ʻ', '\'').Replace('‘', '\'').Replace('’', '\'').Replace('`', '\'');

    public static int? ParseAge(string t)
    {
        t = Norm(t);
        if (Year().Match(t) is { Success: true } y)                       // "2010-yilda tug'ilganman" → age
        {
            var age = DateTime.UtcNow.Year - int.Parse(y.Value);
            return age is >= 4 and <= 99 ? age : null;
        }
        var m = Digits().Match(t);
        if (m.Success && int.TryParse(m.Value, out var d)) return d is >= 4 and <= 99 ? d : null;
        // number words (uz / ru / en): "o'n besh", "двадцать два", "twenty one"
        var total = 0; var any = false;
        foreach (Match w in Word().Matches(t.ToLowerInvariant().Replace('’', '\'').Replace('ʻ', '\'')))
            if (Numbers.TryGetValue(w.Value, out var v)) { total += v; any = true; }
        return any && total is >= 4 and <= 99 ? total : null;
    }

    private static readonly Dictionary<string, int> Numbers = new()
    {
        ["bir"] = 1, ["ikki"] = 2, ["uch"] = 3, ["to'rt"] = 4, ["tort"] = 4, ["besh"] = 5, ["olti"] = 6, ["yetti"] = 7,
        ["sakkiz"] = 8, ["to'qqiz"] = 9, ["toqqiz"] = 9, ["o'n"] = 10, ["on"] = 10, ["yigirma"] = 20, ["o'ttiz"] = 30,
        ["ottiz"] = 30, ["qirq"] = 40, ["ellik"] = 50, ["oltmish"] = 60, ["yetmish"] = 70, ["sakson"] = 80, ["to'qson"] = 90,
        ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10,
        ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13, ["четырнадцать"] = 14, ["пятнадцать"] = 15,
        ["шестнадцать"] = 16, ["семнадцать"] = 17, ["восемнадцать"] = 18, ["девятнадцать"] = 19, ["двадцать"] = 20,
        ["тридцать"] = 30, ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60, ["один"] = 1, ["два"] = 2, ["три"] = 3,
        ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11,
        ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60,
        ["one"] = 1, ["two"] = 2, ["three"] = 3,
    };

    public static string? ParseCity(string t)
    {
        var s = CityNoise().Replace(t, " ");
        s = CitySuffix().Replace(s, "");
        s = Regex.Replace(s, @"[^\p{L}\s'\-ʻ’]", " ").Trim();
        s = Regex.Replace(s, @"\s+", " ");
        if (s.Length is < 2 or > 60) return null;
        var words = s.Split(' ');
        if (words.Length > 3) s = string.Join(' ', words.TakeLast(2));
        return string.Join(' ', s.Split(' ').Select(Cap));
    }

    private static string Cap(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..];

    /// <summary>"Men o'g'il bolaman" / "qizman" / "я девочка" — a short statement about oneself, any time.</summary>
    public static string? GenderStatement(string text)
    {
        var t = Norm(text);
        if (t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5 || !SelfStatement().IsMatch(t)) return null;
        return Boy().IsMatch(t) ? "male" : Girl().IsMatch(t) ? "female" : null;
    }

    // ---------------- state machine ----------------
    /// <summary>Applies this turn's answer and returns the next step + which line to say.</summary>
    public static Outcome Advance(int step, Facts f, bool hasAge, bool hasGender, bool hasCity, int retries)
    {
        if (f.Skip) return new Outcome(Done, "skip", f);
        hasAge |= f.Age is not null; hasGender |= f.Gender is not null; hasCity |= f.City is not null;

        int Next() => !hasAge ? AskAge : !hasGender ? AskGender : !hasCity ? AskCity : Done;

        if (step == Start)                                      // the very first turn: introduce + first question
            return new Outcome(Next(), Next() == Done ? "done" : "intro", f);

        var answered = step switch { AskAge => f.Age is not null, AskGender => f.Gender is not null, AskCity => f.City is not null, _ => true };
        if (!answered && retries < 1) return new Outcome(step, "retry", f);   // ask once more, then move on
        var next = Next();
        if (!answered && next == step) next = step == AskAge ? (hasGender ? (hasCity ? Done : AskCity) : AskGender)
                                       : step == AskGender ? (hasCity ? Done : AskCity) : Done;
        return new Outcome(next, next == Done ? "done" : "next", f);
    }

    // ---------------- lines ----------------
    private static string Q(string lang, int step) => (lang, step) switch
    {
        ("ru-RU", AskAge) => "Сколько тебе лет?",
        ("ru-RU", AskGender) => "Ты мальчик или девочка? Можешь сказать или нажать кнопку на экране.",
        ("ru-RU", AskCity) => "Из какого ты города?",
        ("en-US", AskAge) => "How old are you?",
        ("en-US", AskGender) => "Are you a boy or a girl? You can say it or tap a button on the screen.",
        ("en-US", AskCity) => "Which city are you from?",
        (_, AskAge) => "Necha yoshdasiz?",
        (_, AskGender) => "O'g'il bolamisiz yoki qiz bola? Ayting yoki ekrandagi tugmani bosing.",
        (_, AskCity) => "Qaysi shahardansiz?",
        _ => "",
    };

    private static string Retry(string lang, int step) => (lang, step) switch
    {
        ("ru-RU", AskAge) => "Не расслышал. Скажи возраст числом, например: пятнадцать.",
        ("ru-RU", AskGender) => "Не понял. Просто скажи: мальчик или девочка.",
        ("ru-RU", _) => "Не расслышал. Скажи название города, например: Ташкент.",
        ("en-US", AskAge) => "Sorry, I missed that. Say your age as a number, for example: fifteen.",
        ("en-US", AskGender) => "Sorry? Just say: boy or girl.",
        ("en-US", _) => "Sorry, I missed that. Say your city, for example: Tashkent.",
        (_, AskAge) => "Tushunmadim. Yoshingizni son bilan ayting, masalan: o'n besh.",
        (_, AskGender) => "Tushunmadim. Shunchaki ayting: o'g'il bola yoki qiz bola.",
        _ => "Tushunmadim. Shahringiz nomini ayting, masalan: Toshkent.",
    };

    /// <summary>Uzbek / Russian / English warm address by gender + age.</summary>
    public static string Address(string lang, string? gender, int? age, string name) => (lang, gender) switch
    {
        ("ru-RU", _) => name,
        ("en-US", _) => name,
        (_, "male") when age is < 18 => "o'g'lim",
        (_, "female") when age is < 18 => "qizim",
        (_, "male") => $"{name} uka",
        (_, "female") => $"{name}xon",
        _ => name,
    };

    private static string Intro(string lang, string name) => lang switch
    {
        "ru-RU" => $"Привет, {name}! Я Teacher — твой учитель. Давай быстро познакомимся.",
        "en-US" => $"Hello, {name}! I'm Teacher, your mentor. Let's get to know each other quickly.",
        _ => $"Assalomu alaykum, {name}! Men Teacher — sizning ustozingiz. Keling, tezgina tanishib olaylik.",
    };

    private static string Ack(string lang, Facts f) => (lang, f) switch
    {
        ("ru-RU", { Age: { } a }) => $"{a} — отличный возраст для учёбы!",
        ("ru-RU", _) => "Отлично.",
        ("en-US", { Age: { } a }) => $"{a} — a great age to learn!",
        ("en-US", _) => "Great.",
        (_, { Age: { } a }) when a < 13 => $"{a} yosh — zo'r! Kichkina, lekin aqlli ekansiz.",
        (_, { Age: { } a }) => $"{a} yosh — o'qish uchun ajoyib yosh!",
        (_, { Gender: "male" }) => "Zo'r, demak o'g'il bola.",
        (_, { Gender: "female" }) => "Yaxshi, demak qiz bola.",
        _ => "Ajoyib.",
    };

    private static string Finish(string lang, string addr, string? city, bool skipped) => (lang, skipped) switch
    {
        ("ru-RU", true) => "Хорошо, познакомимся потом. Давай начнём урок — что сегодня изучаем?",
        ("en-US", true) => "Okay, we'll get acquainted later. Let's start — what are we learning today?",
        (_, true) => "Mayli, keyinroq tanishamiz. Qani, darsni boshlaymiz — bugun nimani o'rganamiz?",
        ("ru-RU", _) => $"Приятно познакомиться, {addr}!{(city is null ? "" : $" {city} — прекрасный город.")} Ну что, начинаем урок? Что сегодня изучаем?",
        ("en-US", _) => $"Nice to meet you, {addr}!{(city is null ? "" : $" {city} — lovely city.")} So, let's begin. What are we learning today?",
        _ => $"Tanishganimdan juda xursandman, {addr}!{(city is null ? "" : $" {city} — ajoyib shahar.")} Endi sizni yaxshi bilaman. Qani, darsni boshlaymiz — bugun nimani o'rganamiz?",
    };

    /// <summary>Scripted reply in the v2 contract.</summary>
    public static async IAsyncEnumerable<string> StreamAsync(Outcome o, string lang, string name, string? gender, int? age,
        string? city, int rage, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        object Seg(string emotion, string text) => new { type = "speech", emotion, text, intensity = 1 };
        object Cue(string cue, int segment, double at) => new { cue, segment, at };
        var segs = new List<object>();
        var cues = new List<object>();
        switch (o.Kind)
        {
            case "intro":
                segs.Add(Seg("encouraging", Intro(lang, name)));
                segs.Add(Seg("neutral", Q(lang, o.NextStep)));
                cues.Add(Cue("mouth_smile", 0, 0)); cues.Add(Cue("head_nod", 0, 0.4)); cues.Add(Cue("brow_raise", 1, 0.6));
                break;
            case "retry":
                segs.Add(Seg("neutral", Retry(lang, o.NextStep)));
                cues.Add(Cue("curious_inspect", 0, 0)); cues.Add(Cue("head_tilt", 0, 0.2));
                break;
            case "next":
                segs.Add(Seg("encouraging", Ack(lang, o.Facts)));
                segs.Add(Seg("neutral", Q(lang, o.NextStep)));
                cues.Add(Cue("head_nod", 0, 0.1)); cues.Add(Cue("brow_raise", 1, 0.5));
                break;
            case "fixed":
                segs.Add(Seg("apologetic", lang switch
                {
                    "ru-RU" => $"Понял, исправил! Теперь я знаю: {(gender == "male" ? "ты парень" : "ты девушка")}.",
                    "en-US" => $"Got it, fixed! Now I know you're a {(gender == "male" ? "boy" : "girl")}.",
                    _ => $"Kechirasiz, tuzatib qo'ydim! Endi bilaman: {(gender == "male" ? "siz yigitsiz" : "siz qizsiz")}, {Address(lang, gender, age, name)}.",
                }));
                cues.Add(Cue("head_nod", 0, 0.2)); cues.Add(Cue("mouth_smile", 0, 0.5));
                break;
            default:                                           // done / skip
                segs.Add(Seg("encouraging", Finish(lang, Address(lang, gender, age, name), city, o.Kind == "skip")));
                cues.Add(Cue("mouth_smile", 0, 0)); cues.Add(Cue("head_nod", 0, 0.5));
                break;
        }
        var reply = new
        {
            emotion = "teaching", rageLevel = Math.Max(0, rage - 1), shyLevel = 0, userMood = "neutral", replyLanguage = lang,
            eyeAndFaceCues = cues, pivotToTopic = "",
            bodyAction = new { action = "face_front", beforeSegment = -1 },
            speechSegments = segs,
            safety = new { category = "none", note = "" },
            imageRequest = new { action = "none", prompt = "", count = 1, sourceImageIndex = 0 },
            boardContent = new { show = false, type = "grammar", title = "", markdownBody = "", highlightWords = Array.Empty<string>() },
            quiz = new { active = false, question = "", expectedAnswer = "", timeLimitSeconds = 10 },
            quizResult = new { graded = false, correct = false, feedback = "" },
            mistakes = Array.Empty<object>(), homeworkUpdates = Array.Empty<object>(), assignHomework = Array.Empty<object>(),
            progressUpdates = Array.Empty<object>(), testResults = Array.Empty<object>(),
        };
        yield return JsonSerializer.Serialize(reply);
    }

    /// <summary>"## O'QUVCHI" section for the LLM brief: who Teacher is talking to + how to address them.</summary>
    public static string RenderStudent(string name, string? gender, int? age, string? city)
    {
        name = Mentor.PromptData.LooksLikeInstruction(name) ? "o'quvchi" : System.Text.RegularExpressions.Regex.Replace(name, @"[^\p{L}\s'\-]", "").Trim();
        if (name.Length == 0) name = "o'quvchi";
        var g = gender switch { "male" => "o'g'il bola / yigit", "female" => "qiz bola / ayol", _ => "noma'lum" };
        var addr = (gender, age) switch
        {
            ("male", < 18) => "o'zbekchada \"o'g'lim\", \"uka\" deb murojaat qil",
            ("female", < 18) => "o'zbekchada \"qizim\", \"singlim\" deb murojaat qil",
            ("male", _) => $"o'zbekchada \"{name} uka\" yoki ismi bilan",
            ("female", _) => $"o'zbekchada \"{name}xon\" yoki \"singlim\"",
            _ => "ismi bilan murojaat qil",
        };
        var ru = gender switch
        {
            "male" => "Ruschada mujskoy rod: «ты пришёл, ты молодец, ты готов».",
            "female" => "Ruschada jenskiy rod: «ты пришла, ты умница, ты готова».",
            _ => "",
        };
        var kid = age is < 16
            ? $"\nO'quvchi {age} yoshda — BOLA. Hech qanday so'kinish, haqorat, qo'pol so'z YO'Q (uslub qanday bo'lmasin). Sodda so'zlar, qisqa gaplar, ko'p rag'bat."
            : "";
        return $"\n## O'QUVCHI\nIsmi: {Mentor.PromptData.Q(name, 40)}. Jinsi: {g}. Yoshi: {(age is null ? "noma'lum" : age)}. Shahri: {Mentor.PromptData.Q(city, 60)}.\n" +
               $"Murojaat: {addr}. {ru}{kid}\nMisollarni o'quvchining hayotiga yaqin qil (shahri, yoshi). Talabchanlik jinsga qarab O'ZGARMAYDI.\n";
    }

    [GeneratedRegex(@"\b(keyinroq|keyin\s+aytaman|o'tkazib\s+yubor\w*|kerak\s+emas|aytmayman|потом|пропусти\w*|не\s+хочу|skip|later|not\s+now)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Skip();
    [GeneratedRegex(@"(?:ismim|mening\s+ismim|меня\s+зовут|my\s+name\s+is)\s+(?<n>[\p{L}'ʻ’\-]{2,30})", RegexOptions.IgnoreCase)]
    private static partial Regex MyName();
    [GeneratedRegex(@"\b(o'g'il\w*|og'il\w*|ogil\w*|o'g'lon|erkak\w*|yigit\w*|мальчик|парень|мужчина|мужской|boy|male)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Boy();
    [GeneratedRegex(@"\b(qiz(\s*bola\w*|man|miz)?|ayol\w*|девочка|девушка|женщина|женский|girl|female|woman)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Girl();
    [GeneratedRegex(@"(?<!\d)\d{1,2}(?!\d)")]
    private static partial Regex Digits();
    [GeneratedRegex(@"(?<!\d)(19[3-9]\d|20[0-2]\d)(?!\d)")]
    private static partial Regex Year();
    [GeneratedRegex(@"\b(men|man|bolaman|qizman|erkakman|ayolman|yigitman|я|i\s*'?m|i\s+am)\b|(man|bolaman)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelfStatement();
    [GeneratedRegex(@"[\p{L}']+")]
    private static partial Regex Word();
    [GeneratedRegex(@"\b(men|man|meni|mening|shahrim|shahar|shahridan|shahridanman|dan|danman|yashayman|turaman|я|из|города|город|живу|в|i'm|i\s+am|from|the|city|of|live|in|teacher|ustoz)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CityNoise();
    [GeneratedRegex(@"(dan(man|miz)?|da\s+yashayman|liman|likman)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CitySuffix();
}
