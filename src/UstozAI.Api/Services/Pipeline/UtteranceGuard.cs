using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UstozAI.Api.Services.Pipeline;

/// <summary>Why a voice turn was judged incomplete ("chala gap").</summary>
public enum CutoffReason { TooShort, Silent, Unclear, Filler, TrailingOff, ClippedMidWord }

/// <summary>
/// Incomplete-utterance detector. Runs after STT on voice turns only (typed text is never "cut off").
/// Cheap rules, no model call — the whole point is to answer *instantly* instead of guessing.
/// </summary>
public static partial class UtteranceGuard
{
    // hesitation / filler words (uz, ru, en) — a turn made only of these carries no question
    private static readonly HashSet<string> Fillers =
    [
        "ustoz", "haligi", "anu", "anavi", "nima", "edi", "nimaydi", "eee", "ee", "e", "mmm", "mm", "hmm", "xullas",
        "masalan", "aytmoqchi", "endi", "uh", "um", "erm", "so", "well", "ну", "это", "короче", "типа", "вот", "эээ",
        "ha", "xo'sh", "xosh", "qarang", "bilasizmi",
    ];
    // a sentence that ENDS on one of these was cut mid-thought
    private static readonly HashSet<string> Dangling =
    [
        "va", "lekin", "ammo", "keyin", "chunki", "agar", "yoki", "bilan", "uchun", "deb", "ham", "esa", "haligi", "nima",
        "and", "but", "because", "if", "or", "the", "a", "to", "и", "но", "потому", "если", "или",
    ];

    /// <param name="clippedEnd">Client: voice energy was still high in the last ~200 ms when Space was released.</param>
    public static CutoffReason? Check(string transcript, double? confidence, int? durationMs, int? voicedMs, bool clippedEnd)
    {
        if (durationMs is < 800) return CutoffReason.TooShort;
        if (voicedMs is < 250) return CutoffReason.Silent;

        var raw = transcript.Trim();
        if (raw.Length == 0) return CutoffReason.Unclear;
        var words = Words().Matches(raw.ToLowerInvariant()).Select(m => m.Value.Trim('\'', '’', 'ʻ')).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return CutoffReason.Unclear;

        if (words.All(Fillers.Contains)) return CutoffReason.Filler;                       // "ustoz… haligi…"
        if (raw.EndsWith("...") || raw.EndsWith('…'))
            return words.Count <= 4 ? CutoffReason.TrailingOff : null;                      // long thought + "…" is fine
        if (Dangling.Contains(words[^1]) && (clippedEnd || words.Count <= 6)) return CutoffReason.TrailingOff;
        if (clippedEnd && words.Count <= 2) return CutoffReason.ClippedMidWord;
        if (confidence is < 0.35 && words.Count <= 3) return CutoffReason.Unclear;
        return null;
    }

    [GeneratedRegex(@"[\p{L}\p{N}'’ʻ]+")] private static partial Regex Words();
}

/// <summary>
/// Instant, scripted reaction to a cut-off utterance. Emits the SAME structured JSON as the LLM, so the normal
/// pipeline (TTS, face cues, rage bookkeeping, persistence) handles it — just without waiting for a model.
/// Escalates when the student keeps doing it; vocabulary still goes through InsultFilter.
/// </summary>
public static class CutoffReplies
{
    // [language][style] → [ first time, again, third+ ]
    private static readonly Dictionary<string, Dictionary<string, string[][]>> Lines = new()
    {
        ["uz-UZ"] = new()
        {
            ["soft"] =
            [
                ["Gapingiz chala qoldi-ku. Oxirigacha, to'liq ayting, tinglayapman."],
                ["Yana chala qoldi. Space ni gap tugaguncha bosib turing, shoshilmang."],
                ["Uchinchi marta chala gap. Bir nafas oling, keyin bitta to'liq jumla bilan so'rang."],
            ],
            ["strict"] =
            [
                ["Ey, gapingni oxirigacha ayt! Nimani so'ramoqchisan, chala-chulpa qilmasdan to'liq gapir!",
                 "Bu nima, telegrammami? To'liq gap bilan so'ra!"],
                ["Yana chala! Space ni qo'yib yuborma, fikringni tugat, keyin qo'yvor!",
                 "Ikkinchi marta-ya! Gapni boshidan oxirigacha ayt, men fol ochmayman!"],
                ["Bas! Uchinchi marta chala gap. Hozir o'yla, keyin bitta to'liq savol ber."],
            ],
            ["savage"] =
            [
                ["La'nati, gapingni oxirigacha ayt! O'zi nimani so'ramoqchisan, chala-chulpa qilmasdan to'liq gapir!",
                 "Nima bu, yarim gap? Men fol ochmayman, to'liq gapir!"],
                ["Yana-ya?! Gapingni yarmida tashlab ketasan-a, ahmoq! Space ni bosib tur, gapni tugat!"],
                ["Uchinchi marta! Durakmisan? Bitta to'liq jumla — shu xolos. Qani, boshidan!"],
            ],
        },
        ["ru-RU"] = new()
        {
            ["soft"] =
            [
                ["Вы не договорили. Скажите, пожалуйста, полностью — я слушаю."],
                ["Опять оборвалось. Держите пробел, пока не закончите фразу."],
                ["Третий раз не договариваете. Вдохните и задайте один полный вопрос."],
            ],
            ["strict"] =
            [
                ["Эй, договори до конца! Что ты хочешь спросить? Говори полностью, без огрызков!",
                 "Это что, телеграмма? Задай нормальный полный вопрос!"],
                ["Опять недоговорил! Не отпускай пробел, пока не закончишь мысль!"],
                ["Всё, хватит! Третий раз обрывок. Подумай и задай один полный вопрос."],
            ],
            ["savage"] =
            [
                ["Чёрт возьми, договори до конца! Что ты вообще хочешь спросить? Говори нормально, полностью!"],
                ["Опять?! Бросаешь фразу на полуслове, дурак! Держи пробел и договаривай!"],
                ["Третий раз! Ты издеваешься? Одно полное предложение — и всё. Давай заново!"],
            ],
        },
        ["en-US"] = new()
        {
            ["soft"] =
            [
                ["You didn't finish your sentence. Please say the whole thing — I'm listening."],
                ["Cut off again. Keep holding Space until you've finished speaking."],
                ["Third time it got cut off. Take a breath, then ask one complete question."],
            ],
            ["strict"] =
            [
                ["Hey, finish your sentence! What are you actually asking? Say it in full, not in bits!",
                 "What is this, a telegram? Ask a proper, complete question!"],
                ["Half a sentence again! Don't let go of Space until you're done!"],
                ["Enough! Third broken fragment. Think first, then ask one complete question."],
            ],
            ["savage"] =
            [
                ["Damn it, finish your sentence! What do you even want to ask? Say it properly, all of it!"],
                ["Again?! You keep dropping it halfway! Hold Space and finish the sentence!"],
                ["Third time! Are you kidding me? One complete sentence — that's all. From the top!"],
            ],
        },
    };

    /// <summary>
    /// Space held, nothing said. Many different lines per language × style, so it never sounds like a recording;
    /// the last line Teacher said is never repeated.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string[]>> SilentLines = new()
    {
        ["uz-UZ"] = new()
        {
            ["soft"] =
            [
                "Jim bo'lib qoldingizmi? Bemalol, shoshilmang — tayyor bo'lsangiz gapiring.",
                "Hech narsa eshitmadim. Mikrofon yoqilganmi? Bir tekshirib ko'ring-chi.",
                "Space'ni bosib turib gapiring, gapni tugatgach qo'yib yuboring — men tinglayman.",
                "O'ylab olyapsizmi? Yaxshi, men kutaman. Savolingiz tayyor bo'lsa, ayting.",
                "Ovozingiz kelmadi. Balki biroz balandroq gapirarsiz?",
                "Hech gap yo'q, hammada bo'ladi. Qani, yana bir urinib ko'ramiz.",
            ],
            ["strict"] =
            [
                "Space'ni bosding-u, indamading. Gapir, vaqtimiz ketyapti!",
                "Jimjitlik eshitdim, xolos. Savolingni ayt!",
                "Mikrofon yoqiq, sen esa jimsan. Nima, imtihonmi bu?",
                "Bosib turib jim turish — dars emas. Qani, gapir!",
                "Hech narsa eshitilmadi. Ovozingni chiqar, uyalma!",
                "Yana jimlik. Fikringni yig'ib ol-da, keyin bos.",
            ],
            ["savage"] =
            [
                "Ey, bosding-u jim turibsan! Gapir, vaqtimni olma!",
                "Jimjitlik! Meni sinayapsanmi? Ovozingni chiqar!",
                "Bu nima — telefonda nafas olib turadiganlardanmisan? Gapir!",
                "Space bosilgan, og'iz yopiq. Ajoyib kombinatsiya! Gapir endi!",
                "Hech narsa! Yana hech narsa! Bir og'iz so'z ayt, axir!",
                "Indamay turaversang, men ham indamay turaman. Lekin dars o'tmaydi!",
            ],
        },
        ["ru-RU"] = new()
        {
            ["soft"] =
            [
                "Задумался? Ничего, не спеши — говори, когда будешь готов.",
                "Я ничего не услышал. Микрофон включён? Проверь, пожалуйста.",
                "Держи пробел, пока говоришь, и отпусти в конце — я слушаю.",
                "Не торопись. Как только вопрос будет готов — скажи.",
                "Голоса не было слышно. Может, чуть громче?",
                "Бывает. Давай попробуем ещё раз.",
            ],
            ["strict"] =
            [
                "Нажал пробел и молчишь. Говори, время идёт!",
                "Я слышал только тишину. Задавай вопрос!",
                "Микрофон включён, а ты молчишь. Это что, экзамен на молчание?",
                "Молчать с нажатым пробелом — это не урок. Говори!",
                "Ничего не слышно. Голос, пожалуйста!",
                "Опять тишина. Собери мысль, потом нажимай.",
            ],
            ["savage"] =
            [
                "Эй, нажал и молчишь! Говори, не трать моё время!",
                "Тишина! Ты меня проверяешь? Голос давай!",
                "Ты что, из тех, кто молча дышит в трубку? Говори!",
                "Пробел нажат, рот закрыт. Гениально! Говори уже!",
                "Ничего! Опять ничего! Скажи хоть слово!",
                "Будешь молчать — и я буду молчать. Но урок сам себя не проведёт!",
            ],
        },
        ["en-US"] = new()
        {
            ["soft"] =
            [
                "Thinking it over? No rush — speak whenever you're ready.",
                "I didn't hear anything. Is your microphone on? Please check.",
                "Hold Space while you talk and let go at the end — I'm listening.",
                "Take your time. When your question is ready, just say it.",
                "Your voice didn't come through. Maybe a little louder?",
                "It happens. Let's try once more.",
            ],
            ["strict"] =
            [
                "You pressed Space and said nothing. Speak — time's ticking!",
                "All I heard was silence. Ask your question!",
                "Mic on, mouth shut. Is this a silence exam?",
                "Holding Space in silence is not a lesson. Talk!",
                "Nothing came through. Use your voice!",
                "Silence again. Gather your thoughts, then press.",
            ],
            ["savage"] =
            [
                "Hey, you pressed it and went quiet! Talk — don't waste my time!",
                "Silence! Are you testing me? Use your voice!",
                "Are you one of those people who breathe into the phone? Speak!",
                "Space pressed, mouth closed. Brilliant combo! Talk already!",
                "Nothing! Again nothing! Say one word, come on!",
                "If you stay quiet, I'll stay quiet. But the lesson won't teach itself!",
            ],
        },
    };

    /// <summary>A random line from <paramref name="pool"/> that is not the one said last time.</summary>
    public static string PickFresh(IReadOnlyList<string> pool, string? avoid)
    {
        var fresh = pool.Where(l => avoid is null || !avoid.Contains(l, StringComparison.Ordinal)).ToList();
        if (fresh.Count == 0) fresh = pool.ToList();
        return fresh[Random.Shared.Next(fresh.Count)];
    }

    private static string Opening(CutoffReason r, string fragment, string lang, string style) => style == "soft"
        ? lang switch
        {
            "ru-RU" => "Извините, я не расслышал до конца.",
            "en-US" => "Sorry, I didn't quite catch that.",
            _ => "Kechirasiz, gapingiz to'liq eshitilmadi.",
        }
        : (lang, r) switch
    {
        ("ru-RU", CutoffReason.TooShort) => "Ты даже не успел ничего сказать!",
        ("ru-RU", CutoffReason.Silent) => "Я слышал только тишину.",
        ("ru-RU", CutoffReason.Unclear) => "Ничего не понял.",
        ("ru-RU", _) when fragment.Length is > 0 and <= 60 => $"«{fragment.TrimEnd('.', '…', ' ')}»… и? Дальше что?",
        ("ru-RU", _) => "Ты оборвал фразу на середине.",
        ("en-US", CutoffReason.TooShort) => "You didn't even get a word out!",
        ("en-US", CutoffReason.Silent) => "All I heard was silence.",
        ("en-US", CutoffReason.Unclear) => "I didn't catch any of that.",
        ("en-US", _) when fragment.Length is > 0 and <= 60 => $"\"{fragment.TrimEnd('.', '…', ' ')}\"… and? Then what?",
        ("en-US", _) => "You stopped in the middle of the sentence.",
        (_, CutoffReason.TooShort) => "Hech narsa ulgurmading-ku!",
        (_, CutoffReason.Silent) => "Jimjitlik eshitdim, xolos.",
        (_, CutoffReason.Unclear) => "Hech narsa tushunmadim.",
        _ when fragment.Length is > 0 and <= 60 => $"\"{fragment.TrimEnd('.', '…', ' ')}\"… va? Davomi qani?",
        _ => "Gapingni o'rtasida qoldirding.",
    };

    public static async IAsyncEnumerable<string> StreamAsync(CutoffReason reason, string fragment, string style, int rage,
        int repeat, string? activeTopic, string language, [EnumeratorCancellation] CancellationToken ct, string? lastReply = null)
    {
        await Task.Yield();
        var silent = reason == CutoffReason.Silent;
        string line;
        if (silent)
        {
            var sp = SilentLines.GetValueOrDefault(language) ?? SilentLines["uz-UZ"];
            line = PickFresh(sp.GetValueOrDefault(style) ?? sp["strict"], lastReply);
        }
        else
        {
            var byStyle = Lines.GetValueOrDefault(language) ?? Lines["uz-UZ"];
            var pool = byStyle.GetValueOrDefault(style) ?? byStyle["strict"];
            line = PickFresh(pool[Math.Min(repeat, pool.Length - 1)], lastReply);
        }
        // The FACE must match the WORDS: each style has its own emotion, cues, vocal reaction and rage step.
        //   soft   → patient, kind (no anger rise)      strict → annoyed, exasperated      savage → fiery
        var look = style switch
        {
            "soft" => new
            {
                top = "teaching", seg1 = "teaching", seg2 = repeat >= 2 ? "thinking" : "teaching",
                sfx = "sigh_sfx", sfxText = "Hmm…", sfxIntensity = 1, rageStep = 0,
                cues = new object[] { new { cue = "slow_blink", segment = 1, at = 0.1 }, new { cue = "head_nod", segment = 2, at = 0.3 } },
            },
            "savage" => new
            {
                top = "angry_flame", seg1 = "angry_flame", seg2 = "angry_flame",
                sfx = repeat > 0 ? "sigh_sfx" : "scoff_sfx", sfxText = repeat > 0 ? "Hhh!" : "Hmph!", sfxIntensity = 3,
                rageStep = 2 + Math.Min(repeat, 2),
                cues = new object[] { new { cue = "brow_furrow", segment = 0, at = 0.0 }, new { cue = "wide_eyes", segment = 1, at = 0.0 },
                                      new { cue = "head_shake", segment = 2, at = 0.2 } },
            },
            _ => new
            {
                top = "annoyed_cutoff", seg1 = "annoyed_cutoff", seg2 = repeat >= 2 ? "angry_flame" : "annoyed_cutoff",
                sfx = repeat > 0 ? "sigh_sfx" : "scoff_sfx", sfxText = repeat > 0 ? "Uhh…" : "Hmph.", sfxIntensity = 2,
                rageStep = 1 + Math.Min(repeat, 2),
                cues = new object[] { new { cue = "exasperated_roll", segment = 0, at = 0.0 }, new { cue = "forehead_crease", segment = 1, at = 0.0 },
                                      new { cue = "squint_annoyed", segment = 2, at = 0.1 } },
            },
        };
        var step = silent ? (style == "soft" ? 0 : Math.Min(1, repeat)) : look.rageStep;      // silence: at most +1
        var newRage = Math.Min(10, rage + step);

        var reply = new
        {
            emotion = look.top,
            rageLevel = newRage, shyLevel = 0, userMood = "neutral", replyLanguage = language,
            eyeAndFaceCues = look.cues,
            pivotToTopic = activeTopic ?? "",
            speechSegments = new object[]
            {
                new { type = look.sfx, emotion = look.seg1, text = look.sfxText, intensity = look.sfxIntensity },
                new { type = "speech", emotion = look.seg1, text = silent ? "" : Opening(reason, fragment, language, style), intensity = 1 },
                new { type = "speech", emotion = look.seg2, text = line, intensity = 1 },
            },
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
