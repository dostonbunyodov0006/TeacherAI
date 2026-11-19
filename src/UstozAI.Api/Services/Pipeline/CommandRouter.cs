using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UstozAI.Api.Services.Pipeline;

/// <summary>Body actions the avatar can perform (contract: header.bodyAction.action).</summary>
public static class BodyActions
{
    public static readonly string[] All =
        ["none", "look_right", "look_left", "look_up", "look_down", "face_front", "sit", "stand", "lie_down", "drink_tea",
         "show_creator"];   // shows the creator card (photo) on screen
}

/// <summary>
/// Spoken commands to Teacher's body ("o'ngga qarang", "садись", "drink some tea", "hazillashdim", "Teacher!").
/// Recognised deterministically in uz / ru / en and answered instantly with a scripted reply in the same JSON
/// contract — no LLM round-trip, so the body reacts the moment Space is released. Anything not matched here
/// goes to the LLM, which can still choose a bodyAction itself.
/// </summary>
public static partial class CommandRouter
{
    public record Command(string Kind, string Action);

    /// <summary>Teacher's name as STT may write it in any of the three languages.</summary>
    [GeneratedRegex(@"\b(teacher|ticher|tichir|тичер|тичэр|учитель|ustoz|устоз)\b", RegexOptions.IgnoreCase)]
    public static partial Regex Name();

    private static readonly (Regex re, string kind, string action)[] Rules =
    [
        (R(@"(seni|sizni)\s+kim\s+(yarat|yasa|qil|tuz|dasturla|ixtiro)\w*|kim\s+(yaratgan|yasagan|tuzgan|dasturlagan)|yaratuvching|yaratuvchingiz|muallif\w*\s+kim|asoschi\w*\s+kim"
           + @"|кто\s+(тебя|вас)\s+(создал|сделал|написал|придумал|разработал)|твой\s+(создатель|автор|разработчик)|кто\s+твой\s+(создатель|автор)"
           + @"|who\s+(made|created|built|developed|programmed|wrote)\s+you|who\s+is\s+your\s+(creator|author|developer|maker)|your\s+(creator|author|maker)"), "creator", "show_creator"),
        (R(@"hazil(lash)?(dim|im)|hazil qildim|шучу|пошутил|прикол|just kidding|i was joking|i'?m joking|got you"), "joke", "face_front"),
        (R(@"choy\w*\s+(ich|ichi|ichib|ichasiz|iching|ichamiz|qil)|choy\s+ichib\s+ol|выпей\w*\s+ча|пей\w*\s+ча|попей\w*\s+ча|чаю|drink\w*\s+(some\s+)?tea|have\s+(some\s+)?tea|tea\s+time"), "tea", "drink_tea"),
        (R(@"\byot(ing|gin|ib\s+ol|sang)?\b|\bdam\s+ol|ляг\w*|ложи\w*|прилеч|lie\s+down|lay\s+down|take\s+a\s+nap"), "lie", "lie_down"),
        (R(@"o['‘’ʻ`]?tir(ing|gin|ib\s+ol)?\b|садис\w*|сядь\w*|присяд\w*|sit\s+down|\bsit\b|take\s+a\s+seat"), "sit", "sit"),
        (R(@"\btur(ing|gin|ib\s+tur)?\b|o['‘’ʻ`]?rningizdan\s+tur|встан\w*|вставай\w*|stand\s+up|get\s+up"), "stand", "stand"),
        // a bare direction is enough: "o'ngga", "onga qara", "tepaga", "pasga qarang", "yerga qaragin" …
        (R(@"\b(o['‘’ʻ`]?n+g+a|onga|ongga)\b|o['‘’ʻ`]?ng\s+tomonga|направо|вправо|вправ\w*\s+посмотр|look\s+(to\s+the\s+)?right|turn\s+right"), "look", "look_right"),
        (R(@"\bchapga\b|chap\s+tomonga|налево|влево|look\s+(to\s+the\s+)?left|turn\s+left"), "look", "look_left"),
        (R(@"\b(tepaga|tepaga\w*|yuqoriga|osmonga|shiftga|tepani)\b|вверх|наверх|look\s+up"), "look", "look_up"),
        (R(@"\b(pastga|pasga|pastka|yerga|polga|pastni)\b|вниз|look\s+down"), "look", "look_down"),
        (R(@"menga\s+qara|bu\s+yoqqa\s+qara|посмотри\s+на\s+меня|look\s+at\s+me"), "look", "face_front"),
    ];

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns a command if the utterance is (mainly) a body command, or just Teacher's name ("Teacher!", "Teacher, salom").
    /// Long sentences that merely contain a keyword ("men o'tirib dars qildim…") are left to the LLM.
    /// </summary>
    public static Command? Match(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        foreach (var (re, kind, action) in Rules)
            if (re.IsMatch(t) && (words <= 8 || kind == "creator")) return new Command(kind, action);
        // A greeting ("salom", "Teacher, salom", "привет", "hi there") → return the greeting, don't "answer a question".
        var noName = Name().Replace(t, "").Trim(' ', ',', '.', '!', '?', '-', '—');
        if (noName.Length > 0 && GreetingOnly().IsMatch(noName) && words <= 6) return new Command("greet", "face_front");
        // Only the name → "Labbay?" — the student is calling Teacher.
        var rest = Name().Replace(t, "").Trim(' ', ',', '.', '!', '?', '-', '—');
        if (Name().IsMatch(t) && (rest.Length == 0 || Greeting().IsMatch(rest)) && words <= 4) return new Command("call", "face_front");
        return null;
    }

    [GeneratedRegex(@"^(salom|assalomu\s+alaykum|hey|hi|hello|привет|здравствуй\w*|eshityapsizmi|ты\s+тут|are\s+you\s+there)\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex Greeting();

    /// <summary>The whole (name-less) utterance is a greeting / "how are you".</summary>
    [GeneratedRegex(@"^(salom(\s+aleykum|\s+alaykum)?|assalomu?\s+alaykum|va\s+alaykum\s+assalom|xayrli\s+(tong|kun|kech)|qalaysiz|qalaysan|yaxshimisiz|yaxshimisan|ishlar\s+qalay|privet|привет|здравствуй\w*|добрый\s+(день|вечер)|доброе\s+утро|как\s+дела|hi|hey|hello|good\s+(morning|afternoon|evening)|how\s+are\s+you)([\s,!.?]+(ustoz|teacher|qalaysiz|как\s+дела|how\s+are\s+you|there))*[\s!.?)]*$", RegexOptions.IgnoreCase)]
    private static partial Regex GreetingOnly();

    // ---------------------------------------------------------------- replies
    private static readonly Dictionary<string, Dictionary<string, string[]>> Say = new()
    {
        ["uz-UZ"] = new()
        {
            ["look"] = ["Qani? Qayerda?", "Nima bor u yoqda? Qani?", "Qani-qani? Hech narsa ko'rinmayapti-ku…"],
            ["joke"] = ["Eh, dalbay! Men rostdan qarabman-a…", "Eh, dalbay-ey! Ustozingni aldaysanmi?", "Hmm, hazilkash… Xo'p, darsga qaytamiz."],
            ["sit"] = ["Xo'p, o'tiraman.", "Mayli, bir o'tiraylik."],
            ["stand"] = ["Turdim! Qani, ishga.", "Xo'p, turdim."],
            ["lie"] = ["Oh, rohat… Faqat besh daqiqa, keyin dars!", "Mayli, bir cho'zilay… lekin uxlab qolmayman!"],
            ["tea_before"] = ["Choy? Juda soz, bir piyola ichay."],
            ["tea_after"] = ["Ahh, zo'r choy ekan! Xo'sh, endi darsni boshlaymiz!"],
            ["call"] = ["Labbay? Eshitaman, gapir.", "Ha, shu yerdaman. Nima gap?", "Labbay! Qulog'im senda."],
            ["greet"] = ["Va alaykum assalom! Yaxshimisan? Bugun nimani o'rganamiz?", "Salom-salom! Kayfiyatlar qalay? Darsga tayyormisan?",
                         "Assalomu alaykum! Ko'rib turganimdan xursandman. Qani, nimadan boshlaymiz?"],
            ["creator"] = ["Meni Doston Bunyodov Bunyodovich yaratgan. Men C# va .NET dasturlash tilida yozilganman. Mana, u kishining rasmi!"],
        },
        ["ru-RU"] = new()
        {
            ["look"] = ["Где? Что там?", "Куда смотреть? Где?", "Где-где? Ничего не вижу…"],
            ["joke"] = ["Эх, балбес! А я ведь правда посмотрел…", "Эх ты, шутник! Учителя обманываешь?", "Хм, шутник… Ладно, вернёмся к уроку."],
            ["sit"] = ["Хорошо, сажусь.", "Ладно, присяду."],
            ["stand"] = ["Встал! Ну, за работу.", "Хорошо, встаю."],
            ["lie"] = ["Ох, благодать… Только пять минут, потом урок!", "Ладно, прилягу… но спать не буду!"],
            ["tea_before"] = ["Чай? Отлично, выпью пиалу."],
            ["tea_after"] = ["Ах, хороший чай! Ну что ж, начнём урок!"],
            ["call"] = ["Да? Слушаю тебя.", "Я здесь. Что случилось?", "Да-да, я весь внимание."],
            ["greet"] = ["Привет-привет! Как настроение? Чем сегодня займёмся?", "Здравствуй! Рад тебя видеть. С чего начнём?",
                         "Привет! Готов к уроку?"],
            ["creator"] = ["Меня создал Достон Бунёдов Бунёдович. Я написан на C# и .NET. Вот его фотография!"],
        },
        ["en-US"] = new()
        {
            ["look"] = ["Where? What's over there?", "Where am I looking? Where?", "Where, where? I don't see anything…"],
            ["joke"] = ["Ugh, you goof! I actually looked…", "Oh, you prankster! Fooling your teacher, huh?", "Hmm, a comedian… Fine, back to the lesson."],
            ["sit"] = ["Alright, I'll sit down.", "Sure, let me take a seat."],
            ["stand"] = ["Up I get! Back to work.", "Okay, standing up."],
            ["lie"] = ["Ahh, bliss… Just five minutes, then the lesson!", "Fine, I'll lie down… but I won't fall asleep!"],
            ["tea_before"] = ["Tea? Lovely, I'll have a cup."],
            ["tea_after"] = ["Ahh, great tea! Right, let's start the lesson!"],
            ["call"] = ["Yes? I'm listening.", "I'm here. What's up?", "Yes, yes — all ears."],
            ["greet"] = ["Hey, hello! How are you doing? What shall we learn today?", "Hi there! Good to see you. Where do we start?",
                         "Hello! Ready for today's lesson?"],
            ["creator"] = ["I was created by Doston Bunyodov Bunyodovich. I'm built with C# and .NET. Here's his photo!"],
        },
    };

    private static string Pick(string lang, string key)
    {
        var arr = (Say.GetValueOrDefault(lang) ?? Say["uz-UZ"])[key];
        return arr[Random.Shared.Next(arr.Length)];
    }

    /// <summary>Scripted reply in the v2 JSON contract (with bodyAction in the header).</summary>
    public static async IAsyncEnumerable<string> StreamAsync(Command cmd, string lang, int rage, string? activeTopic,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        object Seg(string type, string emotion, string text, int intensity = 1) => new { type, emotion, text, intensity };
        object Cue(string cue, int segment, double at) => new { cue, segment, at };

        string emotion; object[] segs; object[] cues; int beforeSegment = -1; var newRage = rage;
        switch (cmd.Kind)
        {
            case "look":
                emotion = "thinking";
                segs = [Seg("speech", "thinking", Pick(lang, "look"))];
                cues = [Cue("surprised_brow_raise", 0, 0.1)];
                break;
            case "joke":
                emotion = "mocking"; newRage = Math.Min(10, rage + 1);
                segs = [Seg("scoff_sfx", "mocking", "Hmph!", 2), Seg("speech", "mocking", Pick(lang, "joke"))];
                cues = [Cue("exasperated_roll", 0, 0.0), Cue("head_shake", 1, 0.3)];
                break;
            case "tea":
                // sip first (client waits on the action before segment 1), then the line about starting the lesson
                emotion = "teaching"; beforeSegment = 1;
                segs = [Seg("speech", "teaching", Pick(lang, "tea_before")), Seg("speech", "teaching", Pick(lang, "tea_after"))];
                cues = [Cue("mouth_smile", 1, 0.0)];
                break;
            case "lie":
                emotion = "shy_flustered";
                segs = [Seg("sigh_sfx", "neutral", "Ahh…", 1), Seg("speech", "neutral", Pick(lang, "lie"))];
                cues = [Cue("slow_blink", 1, 0.3)];
                break;
            case "creator":
                emotion = "teaching"; beforeSegment = -1; newRage = Math.Max(0, rage - 3);   // a proud, warm moment
                segs = [Seg("speech", "encouraging", Pick(lang, "creator"))];
                cues = [Cue("mouth_smile", 0, 0.1), Cue("head_nod", 0, 0.6)];
                break;
            case "greet":
                emotion = "neutral"; newRage = Math.Max(0, rage - 1);
                segs = [Seg("speech", "encouraging", Pick(lang, "greet"))];
                cues = [Cue("mouth_smile", 0, 0.0), Cue("head_nod", 0, 0.25)];
                break;
            case "call":
                emotion = "neutral";
                segs = [Seg("speech", "neutral", Pick(lang, "call"))];
                cues = [Cue("head_nod", 0, 0.1), Cue("brow_raise", 0, 0.0)];
                break;
            default:                                  // sit / stand
                emotion = "neutral";
                segs = [Seg("speech", "neutral", Pick(lang, cmd.Kind))];
                cues = [Cue("head_nod", 0, 0.2)];
                break;
        }

        var reply = new
        {
            emotion, rageLevel = newRage, shyLevel = 0, userMood = cmd.Kind == "joke" ? "amused" : "neutral", replyLanguage = lang,
            eyeAndFaceCues = cues, pivotToTopic = activeTopic ?? "",
            bodyAction = new { action = cmd.Action, beforeSegment },
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
}
