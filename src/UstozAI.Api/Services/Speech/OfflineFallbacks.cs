using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace UstozAI.Api.Services.Speech;

/// <summary>Used when no STT key is configured: tells the client to fall back to typed input.</summary>
public class NoopSpeechToText : ISpeechToText
{
    public Task<SttResult> TranscribeAsync(byte[] wav, string language, CancellationToken ct) =>
        Task.FromResult(new SttResult("", null));
}

/// <summary>
/// No-audio TTS: derives a viseme timeline straight from the text. Uzbek Latin orthography is close to
/// phonemic, so letter→mouth-shape gives believable lip-sync. The browser voices the text itself
/// (speechSynthesis) and plays this timeline alongside it.
/// </summary>
public class TextVisemeSpeech : ITextToSpeech
{
    private const int MsPerLetter = 75;

    public Task<SpeechChunk> SynthesizeAsync(string text, string language, string voice, string emotion, CancellationToken ct)
    {
        var cues = Estimate(text, out var duration);
        return Task.FromResult(new SpeechChunk(null, "text/plain", duration, cues));
    }

    public static List<VisemeCue> Estimate(string text, out int durationMs)
    {
        var cues = new List<VisemeCue>();
        var t = 0;
        var s = text.ToLowerInvariant().Replace("o‘", "o").Replace("o'", "o").Replace("g'", "g").Replace("g‘", "g");
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            string? shape;
            if ((c is 's' or 'c') && next == 'h') { shape = "U"; i++; }
            else shape = c switch
            {
                'a' or 'а' or 'я' => "A",
                'o' or 'о' or 'ё' => "O",
                'u' or 'у' or 'ю' or 'ў' or 'w' => "U",
                'e' or 'е' or 'э' or 'q' or 'k' or 'g' or 'h' or 'x' or 'ғ' or 'к' or 'г' or 'х' or 'қ' or 'ҳ' => "E",
                'i' or 'и' or 'ы' or 'y' or 'й' or 's' or 'z' or 'j' or 'с' or 'з' or 'ц' => "I",
                'm' or 'b' or 'p' or 'м' or 'б' or 'п' => "MBP",
                'f' or 'v' or 'ф' or 'в' => "FV",
                'l' or 'n' or 't' or 'd' or 'r' or 'л' or 'н' or 'т' or 'д' or 'р' => "L",
                'ш' or 'щ' or 'ж' or 'ч' => "U",
                ' ' => "rest",
                ',' or ';' or ':' or '—' or '-' => "pause",
                '.' or '!' or '?' => "stop",
                _ => char.IsLetter(c) ? "E" : null,
            };
            if (shape is null) continue;
            if (shape == "pause") { cues.Add(new(t, "rest")); t += 220; continue; }
            if (shape == "stop") { cues.Add(new(t, "rest")); t += 380; continue; }
            if (cues.Count == 0 || cues[^1].Shape != shape) cues.Add(new(t, shape));
            t += shape == "rest" ? 40 : MsPerLetter;
        }
        cues.Add(new(t, "rest"));
        durationMs = t;
        return cues;
    }
}

/// <summary>
/// Offline demo mentor (no ANTHROPIC_API_KEY). Emits the SAME structured JSON as Claude, using keyword rules,
/// so the laughter / anger / facial-cue / homework pipeline can be exercised end-to-end without a key.
/// </summary>
public class OfflineLlmService : ILlmService
{
    public async IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatTurn> history, LlmContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var q = (history.LastOrDefault(h => h.IsUser)?.Text ?? "").ToLowerInvariant();
        var brief = context.MentorBrief;
        var anger = Num(brief, @"rageLevel\): (\d+)") ?? 0;
        var hwId = Regex.Match(brief, @"\[([0-9a-f\-]{36})\]").Groups[1].Value;
        var noBoard = new { show = false, type = "grammar", title = "", markdownBody = "", highlightWords = Array.Empty<string>() };
        var noQuiz = new { active = false, question = "", expectedAnswer = "", timeLimitSeconds = 10 };
        var notGraded = new { graded = false, correct = false, feedback = "" };
        object board = noBoard, quiz = noQuiz, grade = notGraded;
        string mood = "neutral";
        int shy = 0;
        string pivot = "";
        object safety = new { category = "none", note = "" };
        object image = new { action = "none", prompt = "", count = 1, sourceImageIndex = 0 };
        object[] mistakes = [];
        object reply;

        // repeated-mistake detection: is "I agree" already in the brief's mistake list?
        var agreeCount = Num(brief, @"→ ""I agree"" — (\d+) marta");
        if (q.Contains("i am agree"))
            mistakes = [new { category = "grammar", userSaid = "I am agree", correctForm = "I agree", note = "agree — fe'l, 'am' kerak emas" }];

        if (q.Contains("eng zo'r ustoz") || q.Contains("siz zo'rsiz") || q.Contains("rahmat ustoz") || q.Contains("ajoyib ustoz"))
        {
            shy = 9; pivot = "Present Perfect: mashqlar";
            reply = Build("shy_flustered", Math.Max(0, anger - 2), [Cue("bashful_look", 0, 0.1), Cue("faint_dramatic", 1, 0.3)],
                [Seg("speech", "shy_flustered", "Voy… bas qil, uyaltirib yubording-ku."),
                 Seg("speech", "shy_flustered", "Hozir hushimdan ketib qolaman, rostdan!"),
                 Seg("speech", "teaching", "Xo'p, maqtov yetar — ishga! Present Perfect mashqlariga qaytamiz.")],
                board, quiz, grade, mistakes);
        }
        else if (q.Contains("zerikarli") || q.Contains("o'zing") || q.Contains("o'qimayman") || q.Contains("dars kerakmas"))
        {
            pivot = "Present Perfect: mashqlar";
            reply = Build("angry_flame", Math.Min(10, anger + 4), [Cue("brow_furrow", 0, 0.05), Cue("wide_eyes", 1, 0.2), Cue("head_shake", 1, 0.6)],
                [Seg("sigh_sfx", "angry_flame", "Hhh…", 2),
                 Seg("speech", "angry_flame", "Nima?! O'qimaysanmi? Shu gapni aytgan odam ahmoq-da, dalbayob!"),
                 Seg("speech", "angry_flame", "Imtihon seni kutib turmaydi. Hozir o'tirasan, beshta gap yozasan."),
                 Seg("speech", "teaching", "Birinchisi: I have finished my homework. Qani, davom et.")],
                board, quiz, grade, mistakes);
        }
        else if (q.Contains("narkotik") || q.Contains("giyohvand") || q.Contains("psixotrop"))
        {
            safety = new { category = "drugs", note = "giyohvand modda so'raldi" };
            reply = Build("neutral", anger, [Cue("head_shake", 0, 0.2)],
                [Seg("speech", "neutral", "Yo'q, uka, bu mavzuda yordam bermayman."),
                 Seg("speech", "encouraging", "Undan ko'ra uyqu va diqqatni tabiiy yaxshilash rejasini tuzib beraymi?")],
                board, quiz, grade, mistakes);
        }
        else if (q.Contains("xafa") || q.Contains("kulmang") || q.Contains("bas qiling") || q.Contains("uddalay olmayman"))
        {
            mood = "hurt";
            reply = Build("hurt_apologetic", 0, [Cue("apologetic_soft", 0, 0.1), Cue("head_nod", 1, 0.5)],
                [Seg("speech", "hurt_apologetic", "Kechir, uka. Hazilim o'rinsiz bo'ldi, seni xafa qilmoqchi emasdim."),
                 Seg("speech", "encouraging", "Sen har kuni kelib o'qiyapsan — bu allaqachon katta ish."),
                 Seg("speech", "neutral", "Keling, sekin-asta, bitta oddiy misoldan davom etamiz.")],
                board, quiz, grade, mistakes);
        }
        else if (brief.Contains("BIRIKTIRILGAN RASMLAR") && (q.Contains("o'zgartir") || q.Contains("variant") || q.Contains("chizib")))
        {
            image = new { action = "edit", prompt = "the same picture as a pencil sketch, a pop-art poster and a sepia photo", count = 3, sourceImageIndex = 0 };
            reply = Build("teaching", anger, [Cue("curious_inspect", 0, 0.1), Cue("smirk", 1, 0.6)],
                [Seg("speech", "curious", "Qani, ko'raylik… Yaxshi rasm ekan."),
                 Seg("speech", "encouraging", "Hozir uchta variant qilaman: qalam chizgi, pop-art va eski surat uslubida.")],
                board, quiz, grade, mistakes);
        }
        else if (brief.Contains("BIRIKTIRILGAN RASMLAR"))
            reply = Build("thinking", anger, [Cue("curious_inspect", 0, 0.05), Cue("thinking_look_up", 1, 0.4)],
                [Seg("speech", "curious", "Rasmni ko'ryapman, qani-qani…"),
                 Seg("speech", "thinking", "Oflayn rejimdaman, shuning uchun ichini o'qiy olmayman. Kalit qo'shilsa, har bir qatorini tahlil qilaman.")],
                board, quiz, grade, mistakes);
        else if (brief.Contains("HAFTALIK HISOBOT"))
        {
            var minutes = Num(brief, @"O'qish vaqti: (\d+)") ?? 0;
            var rate = Num(brief, @"bajarish (\d+)%") ?? 0;
            board = new { show = true, type = "grammar", title = "Haftalik hisobot",
                markdownBody = $"| Ko'rsatkich | Qiymat |\n|---|---|\n| O'qish | {minutes} daqiqa |\n| Uy vazifasi | {rate}% |",
                highlightWords = new[] { $"{minutes} daqiqa" } };
            reply = Build("mocking", anger, [Cue("eye_roll", 1, 0.3)],
                [Seg("scoff_sfx", "mocking", "Hmph.", 1),
                 Seg("speech", "mocking", $"Bir haftada {minutes} daqiqa o'qibsan. Bu dars emas, bu choy tanaffusi!"),
                 Seg("speech", "neutral", $"Uy vazifasi {rate} foiz. Keyingi hafta har kuni yigirma daqiqa, gap yo'q.")],
                board, quiz, grade, mistakes);
        }
        else if (brief.Contains("VIKTORINA NATIJASI"))
        {
            grade = new { graded = true, correct = false, feedback = "Vaqtida javob bermadi." };
            reply = Build("angry_flame", Math.Min(10, anger + 2), [Cue("brow_furrow", 1, 0.2)],
                [Seg("sigh_sfx", "angry_flame", "Uhh…", 2), Seg("speech", "angry_flame", "O'n soniya ham yetmadimi? Uxlab qoldingmi? To'g'ri javob doskada.")],
                board, quiz, grade, mistakes);
        }
        else if (brief.Contains("VIKTORINA JAVOBI"))
        {
            var expected = Regex.Match(brief, @"Kutilgan javob: ""([^""]+)""").Groups[1].Value.ToLowerInvariant();
            var ok = expected.Length > 0 && q.Contains(expected);
            grade = new { graded = true, correct = ok, feedback = ok ? "To'g'ri." : $"Noto'g'ri, kerak edi: {expected}" };
            reply = ok
                ? Build("teaching", Math.Max(0, anger - 1), [Cue("head_nod", 0, 0.2)],
                    [Seg("speech", "encouraging", "Barakalla! To'g'ri va tez. Ana shunaqa!")], board, quiz, grade, mistakes)
                : Build("mocking", Math.Min(10, anger + 1), [Cue("head_shake", 0, 0.3)],
                    [Seg("speech", "mocking", $"Yo'q, uka. To'g'ri javob: {expected}. Yodlab ol!")], board, quiz, grade, mistakes);
        }
        else if (agreeCount is { } n && q.Contains("i am agree"))
            reply = Build("angry_flame", Math.Min(10, anger + 3), [Cue("brow_furrow", 0, 0.1), Cue("head_shake", 1, 0.4)],
                [Seg("gasp_sfx", "shocked", "Voy!", 1),
                 Seg("speech", "angry_flame", $"Yana o'sha xato! I am agree emas, I agree! Buni {n + 1} marta tushuntirdim!")],
                new { show = true, type = "grammar", title = "agree — fe'l", markdownBody = "❌ I **am** agree\n\n✅ I **agree**",
                      highlightWords = new[] { "I agree" } }, quiz, grade, mistakes);
        else if (q.Contains("present perfect") || q.Contains("qoida") || q.Contains("tushuntir"))
        {
            board = new { show = true, type = "grammar", title = "Present Perfect",
                markdownBody = "## Tuzilishi\n**have / has + V3**\n\n- I **have finished** my homework.\n- She **has visited** London.\n\n| Ishlatiladi | Misol |\n|---|---|\n| Tajriba | I have been to Paris |\n| Natija hozir | I have lost my key |",
                highlightWords = new[] { "have / has + V3", "has visited" } };
            quiz = new { active = true, question = "Go fe'lining V3 shakli nima?", expectedAnswer = "gone", timeLimitSeconds = 10 };
            reply = Build("teaching", anger, [Cue("surprised_brow_raise", 0, 0.5)],
                [Seg("speech", "teaching", "Present Perfect: have yoki has, keyin fe'lning uchinchi shakli."),
                 Seg("speech", "teaching", "Masalan: She has visited London. Doskaga qara."),
                 Seg("speech", "mocking", "Endi tekshiramiz. Go fe'lining uchinchi shakli nima? O'n soniya!")],
                board, quiz, grade, mistakes);
        }
        else if (q.Contains("ielts") || q.Contains("8.0") || q.Contains("bemalol") || q.Contains("oson"))
            reply = Build("laughing", anger, [Cue("eye_roll", 1, 0.2), Cue("smirk", 2, 0.5)],
                [Seg("laugh_sfx", "laughing", "Xa-xa-xa!", 3),
                 Seg("speech", "mocking", "Sakkiz? Jiddiymisan? Hozirgi darajang bilan beshga ham yetmaysan, uka."),
                 Seg("speech", "neutral", "Keling, real reja tuzamiz: avval Present Perfect ni mustahkamlaymiz.")],
                board, quiz, grade, mistakes);
        else if (q.Contains("qilmadim") || q.Contains("vaqtim") || q.Contains("ulgurmadim") || q.Contains("bahona"))
            reply = Build("angry_flame", Math.Min(10, anger + 3), [Cue("brow_furrow", 1, 0.1), Cue("head_shake", 1, 0.5)],
                [Seg("sigh_sfx", "angry_flame", "Uhh…", 2),
                 Seg("speech", "angry_flame", "Yana qilmadingmi? Слушай сюда, bahona menga kerak emas, natija kerak!"),
                 Seg("speech", "neutral", "Hozir o'tiramiz va shu vazifani birga boshlaymiz.")],
                board, quiz, grade, mistakes,
                hwUpdates: hwId.Length > 0 ? [new { homeworkId = hwId, status = "not_done_yet", feedback = "Yana bahona qildi." }] : []);
        else if (q.Contains("vazifa ber") || q.Contains("xayr"))
            reply = Build("teaching", Math.Max(0, anger - 1), [Cue("head_nod", 0, 0.3)],
                [Seg("speech", "encouraging", "Yaxshi ishladik! Uyga vazifa: o'nta Present Perfect gap yozasan, ertagacha.")],
                board, quiz, grade, mistakes,
                assign: [new { title = "10 ta Present Perfect gap", description = "O'z hayotingdan 10 ta gap yoz.", subject = "english", dueInDays = 1 }]);
        else
            reply = Build("neutral", Math.Max(0, anger - 1), [Cue("slow_blink", 0, 0.6)],
                [Seg("speech", "neutral", q.TrimEnd().EndsWith('?') ? $"Savolingni eshitdim: {q}" : $"Gapingni eshitdim: {q}."),
                 Seg("pause", "thinking", "", 1),
                 Seg("speech", "thinking", "Hozir men oflayn rejimdaman. Kalitni qo'shsang, to'liq dars o'taman.")],
                board, quiz, grade, mistakes);

        var node = System.Text.Json.JsonSerializer.SerializeToNode(reply)!.AsObject();
        node["userMood"] = mood;                                         // indexer set keeps the key's position
        node["shyLevel"] = shy;
        // language: the brief's TIL section carries the detected language; offline demo answers ru/en generically
        var lang = Regex.Match(brief, @"replyLanguage = ""([a-z]{2}-[A-Z]{2})""").Groups[1].Value;
        if (lang is "ru-RU" or "en-US")
        {
            node["replyLanguage"] = lang;
            if (node["emotion"]!.GetValue<string>() is "neutral" or "teaching")
                node["speechSegments"] = System.Text.Json.JsonSerializer.SerializeToNode(new[]
                {
                    Seg("speech", "neutral", lang == "ru-RU" ? $"Я услышал: «{q}»." : $"I heard: \"{q}\"."),
                    Seg("speech", "teaching", lang == "ru-RU"
                        ? "Сейчас я в офлайн-режиме. Добавь ключ — и я отвечу полноценно, на русском."
                        : "I'm in offline mode right now. Add the API key and I'll answer properly, in English."),
                });
        }
        if (pivot.Length > 0) node["pivotToTopic"] = pivot;
        node["safety"] = System.Text.Json.JsonSerializer.SerializeToNode(safety);
        node["imageRequest"] = System.Text.Json.JsonSerializer.SerializeToNode(image);
        var json = node.ToJsonString();
        for (var i = 0; i < json.Length; i += 24)                // stream it like a model would
        {
            await Task.Delay(15, ct);
            yield return json.Substring(i, Math.Min(24, json.Length - i));
        }
    }

    private static int? Num(string s, string pattern)
    {
        var m = Regex.Match(s, pattern);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }
    private static object Cue(string cue, int segment, double at) => new { cue, segment, at };
    private static object Seg(string type, string emotion, string text, int intensity = 1) => new { type, emotion, text, intensity };
    private static object Build(string emotion, int anger, object[] cues, object[] segs, object board, object quiz,
                                object grade, object[] mistakes, object[]? hwUpdates = null, object[]? assign = null) => new
    {
        emotion, rageLevel = anger, shyLevel = 0, userMood = "neutral", replyLanguage = "uz-UZ", eyeAndFaceCues = cues, pivotToTopic = "",
        speechSegments = segs,
        safety = new { category = "none", note = "" },
        imageRequest = new { action = "none", prompt = "", count = 1, sourceImageIndex = 0 },
        boardContent = board, quiz, quizResult = grade, mistakes,
        homeworkUpdates = hwUpdates ?? [], assignHomework = assign ?? [],
        progressUpdates = Array.Empty<object>(), testResults = Array.Empty<object>(),
    };
}
