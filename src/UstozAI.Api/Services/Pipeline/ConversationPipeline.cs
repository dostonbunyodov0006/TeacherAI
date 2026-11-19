using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UstozAI.Api.Data;
using UstozAI.Api.Services.Analytics;
using UstozAI.Api.Services.Conversations;
using UstozAI.Api.Services.Learning;
using UstozAI.Api.Services.Media;
using UstozAI.Api.Services.Speech;
using UstozAI.Api.Services.Mentor;

namespace UstozAI.Api.Services.Pipeline;

/// <summary>Segment metadata attached to each playable chunk.</summary>
public record SegmentInfo(int Index, string Type, string Emotion, int Intensity, bool FirstOfSegment);

/// <summary>Quiz as the client sees it — the expected answer is never sent.</summary>
public record QuizPrompt(Guid Id, string Question, int TimeLimitSeconds);

/// <summary>What kind of turn this is, plus quiz / mic-language context from the client.</summary>
/// <param name="Kind">"normal" | "quiz_timeout" | "weekly_report"</param>
/// <param name="SpeechLanguage">STT language for this utterance (e.g. "en-US" while practising English).</param>
/// <param name="AttachmentIds">Images the user uploaded (POST /api/media) for this turn — max 4.</param>
/// <param name="ClientDurationMs">Recording length measured in the browser.</param>
/// <param name="VoicedMs">How much of it actually contained voice (client energy detector).</param>
/// <param name="ClippedEnd">Voice was still loud in the last ~200 ms when Space was released → probably cut mid-word.</param>
/// <param name="Posture">Teacher's current body posture on screen (stand | sit | lie) → told to the LLM.</param>
public record TurnOptions(Guid? QuizId = null, int? AnswerDelayMs = null, string? SpeechLanguage = null, string Kind = "normal",
                          IReadOnlyList<Guid>? AttachmentIds = null,
                          int? ClientDurationMs = null, int? VoicedMs = null, bool ClippedEnd = false, string? Posture = null);

/// <summary>Event streamed to the browser. <c>Type</c> drives the client state machine.</summary>
public record TurnEvent(string Type)
{
    public string? Text { get; init; }
    public string? State { get; init; }
    public int? Index { get; init; }
    public SpeechChunk? Speech { get; init; }
    public SegmentInfo? Segment { get; init; }
    public MentorHeader? Mentor { get; init; }
    public BoardContent? Board { get; init; }
    public QuizPrompt? Quiz { get; init; }
    public QuizGrade? QuizResult { get; init; }
    public IReadOnlyList<MistakeReport>? Mistakes { get; init; }
    public IReadOnlyList<MediaRef>? Images { get; init; }          // "images": generated/edited results; "attachments": echo of uploads
    public IReadOnlyList<string>? Presets { get; init; }           // "image_filters": offline client-side variations
    public string? SourceUrl { get; init; }
    public PolicyOutcome? Policy { get; init; }
    public Guid? SessionId { get; init; }
    public int? LatencyMs { get; init; }
    public int? RageLevel { get; init; }
    public int? Onboarding { get; init; }                          // "done": current getting-to-know step (2 = ask boy/girl)

    public static TurnEvent StateOf(string s) => new("state") { State = s };
    public static TurnEvent Error(string msg) => new("error") { Text = msg };
}

/// <summary>
/// One turn: audio → STT (+pronunciation) → mentor context (level, homework, anger, mistakes, quiz) →
/// Claude structured JSON (streamed) → speech/FX chunks, live board + quiz events → apply actions → persist.
/// </summary>
public class ConversationPipeline(
    IServiceScopeFactory scopes, ISpeechToText stt, ILlmService llm, ITextToSpeech tts,
    IImageGenerator images, UserTurnGate gate, ILogger<ConversationPipeline> log)
{
    private const int HistoryImageTurns = 3;          // older images become a text placeholder (token budget)
    private static readonly TimeSpan ImageJobTimeout = TimeSpan.FromSeconds(120);
    private static readonly HashSet<string> SpeechLanguages = ["uz-UZ", "ru-RU", "en-US"];

    /// <summary>How long a student's second turn (e.g. another tab) may wait for the first one to finish.</summary>
    private static readonly TimeSpan TurnQueueTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// One turn at a time per student (other students run in parallel). Everything a turn reads and writes —
    /// session, mentor state, homework, messages — is scoped to <paramref name="userId"/>, so data never mixes.
    /// </summary>
    public async IAsyncEnumerable<TurnEvent> RunAsync(Guid userId, Guid? sessionId, string? timeZone,
        byte[]? wav, string? typed, TurnOptions? options, [EnumeratorCancellation] CancellationToken ct)
    {
        IDisposable? turn;
        try { turn = await gate.EnterAsync(userId, TurnQueueTimeout, ct); }
        catch (OperationCanceledException) { yield break; }                 // the student cancelled while waiting
        if (turn is null)
        {
            yield return TurnEvent.Error("Oldingi savolingizga javob hali tayyorlanmoqda — biroz kuting.");
            yield break;
        }
        using (turn)
        {
            await foreach (var e in RunCoreAsync(userId, sessionId, timeZone, wav, typed, options, ct).WithCancellation(ct))
                yield return e;
        }
    }

    private async IAsyncEnumerable<TurnEvent> RunCoreAsync(Guid userId, Guid? sessionId, string? timeZone,
        byte[]? wav, string? typed, TurnOptions? options, [EnumeratorCancellation] CancellationToken ct)
    {
        var opt = options ?? new TurnOptions();
        var clock = Stopwatch.StartNew();
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var conversations = sp.GetRequiredService<ConversationService>();
        var mentor = sp.GetRequiredService<MentorStateService>();
        var learning = sp.GetRequiredService<LearningService>();
        var media = sp.GetRequiredService<MediaStore>();
        var safety = sp.GetRequiredService<SafetyPolicy>();

        var pref = await db.Preferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct)
                   ?? new UserPreference();
        var session = sessionId is { } sid
            ? await conversations.FindOwnedAsync(userId, sid, ct)
            : await conversations.GetOrCreateTodayAsync(userId, timeZone, ct);
        if (session is null)
        {
            yield return TurnEvent.Error("Suhbat topilmadi. Chap paneldan kunni qayta tanlang.");
            yield break;
        }
        var today = ConversationService.LocalToday(ConversationService.ResolveZone(timeZone ?? pref.TimeZone));

        yield return new TurnEvent("state") { State = "thinking", SessionId = session.Id };

        // 1) STT — in the language the student is actually speaking (EN practice ≠ UZ chat)
        string userText;
        IReadOnlyList<PronouncedWord>? words = null;
        CutoffReason? cutoff = null;                 // "chala gap" → instant scripted snap instead of the LLM
        string? turnLang = null;                     // the language the student used THIS turn → reply + voice
        if (wav is not null)
        {
            // "auto" (default): Azure identifies uz/ru/en; an explicit locale (EN drill) enables pronunciation scoring.
            var sttLang = opt.SpeechLanguage is { } l && SpeechLanguages.Contains(l) ? l : "auto";
            var r = await stt.TranscribeAsync(wav, sttLang, ct);
            userText = r.Text.Trim();
            words = r.Words;
            turnLang = r.Language ?? (sttLang != "auto" ? LanguageSupport.Normalize(sttLang) : null);
            cutoff = UtteranceGuard.Check(userText, r.Confidence, opt.ClientDurationMs, opt.VoicedMs, opt.ClippedEnd);
            if (userText.Length > 0) yield return new TurnEvent("transcript") { Text = userText };
            if (userText.Length == 0) userText = "(tushunarsiz gap)";
        }
        else if (opt.Kind == "browser_stt")
        {
            userText = (typed ?? "").Trim();
            cutoff = UtteranceGuard.Check(userText, null, opt.ClientDurationMs, opt.VoicedMs, opt.ClippedEnd);
            turnLang = opt.SpeechLanguage is { } bl && SpeechLanguages.Contains(bl) ? bl : null;
            if (userText.Length > 0) yield return new TurnEvent("transcript") { Text = userText };
            if (userText.Length == 0) userText = "(tushunarsiz gap)";
        }
        else if (opt.Kind == "cutoff")
        {
            // Browser already knew the clip was too short / silent — never uploaded, never transcribed.
            cutoff = UtteranceGuard.Check("", null, opt.ClientDurationMs, opt.VoicedMs, false) ?? CutoffReason.TooShort;
            userText = "(chala gap)";
        }
        else userText = opt.Kind switch
        {
            "quiz_timeout" => "(Vaqt tugadi — javob bera olmadim.)",
            "weekly_report" => "Ustoz, haftalik hisobotimni ayting.",
            _ => (typed ?? "").Trim(),
        };
        turnLang ??= LanguageSupport.Detect(userText) ?? LanguageSupport.Normalize(session.Language);
        var attachments = new List<MediaAsset>();
        foreach (var id in (opt.AttachmentIds ?? []).Distinct().Take(4))
            if (await media.FindOwnedAsync(userId, id, ct) is { Kind: MediaKind.Upload, MessageId: null } a) attachments.Add(a);
        if (userText.Length == 0 && attachments.Count > 0) userText = "(rasm yubordim)";
        if (userText.Length == 0) yield break;
        if (attachments.Count > 0)
            yield return new TurnEvent("attachments") { Images = attachments.Select(media.Ref).ToList() };

        // 2) Context snapshot (BEFORE saving the new message → correct "session start" detection)
        var snapshot = await mentor.SnapshotAsync(userId, session, today, ct);
        var student = await db.Users.FirstAsync(u => u.Id == userId, ct);
        // Children never get the unfiltered "Olovli" style, whatever the switch says.
        if (student.Age is < 16 && snapshot.Style == "savage") snapshot = snapshot with { Style = "strict" };
        if (cutoff is not null) yield return new TurnEvent("cutoff") { Text = cutoff.ToString() };
        // A cut-off answer doesn't consume an open quiz — the student gets to answer properly.
        var quizCtx = cutoff is null
            ? await learning.OpenForAnswerAsync(userId, session.Id, opt.QuizId, opt.AnswerDelayMs, opt.Kind == "quiz_timeout", ct)
            : null;
        var brief = new StringBuilder(MentorStateService.RenderBrief(snapshot, today));
        brief.Append(LearningService.RenderMistakes(await learning.TopMistakesAsync(userId, 8, ct)));
        brief.Append(LearningService.RenderQuiz(quizCtx));
        brief.Append(RenderPronunciation(words));
        if (attachments.Count > 0)
            brief.Append($"\n## BIRIKTIRILGAN RASMLAR\nShu xabarga {attachments.Count} ta rasm biriktirilgan (sourceImageIndex 0..{attachments.Count - 1}).\n");
        brief.Append(Onboarding.RenderStudent(student.DisplayName, student.Gender, student.Age, student.City));
        brief.Append($"\n## TIL\nO'quvchi bu safar {LanguageSupport.NameUz(turnLang)} tilida gapirdi ({turnLang}). " +
                     $"Javobni shu tilda ber, replyLanguage = \"{turnLang}\".\n");
        if (opt.Posture is "sit" or "lie")
            brief.Append($"\n## TANA HOLATI\nHozir sen {(opt.Posture == "sit" ? "o'tiribsan" : "yotibsan")}. Turishni so'rasa — stand.\n");
        if (!string.IsNullOrWhiteSpace(session.ActiveTopic))
            brief.Append($"\n## FAOL MAVZU\n{PromptData.Q(session.ActiveTopic, 80)} — chalg'ishdan keyin shunga qaytar (pivotToTopic).\n");
        if (opt.Kind == "weekly_report")
        {
            var report = await sp.GetRequiredService<WeeklyReportService>().BuildAsync(userId, timeZone, ct);
            brief.Append(WeeklyReportService.RenderForMentor(report));
        }

        var userMsg = new ChatMessage
        {
            Role = MessageRole.User, Content = userText, AudioMs = wav is null ? null : WavMs(wav),
            AttachmentUrl = attachments.Count > 0 ? MediaStore.CanonicalUrl(attachments[0].Id) : null,
        };
        await conversations.AppendAsync(session, userMsg, ct);
        if (attachments.Count > 0)
        {
            var ids = attachments.Select(a => a.Id).ToList();
            await db.Media.Where(m => ids.Contains(m.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.MessageId, userMsg.Id).SetProperty(m => m.SessionId, session.Id), ct);
        }

        // Reply source: Claude, or — for a cut-off utterance — the instant scripted snap (same JSON contract).
        IAsyncEnumerable<string> replySource;
        // Body commands / "Teacher!" calls answer instantly (scripted), like cut-offs — the body moves immediately.
        var command = cutoff is null && opt.Kind is "normal" or "browser_stt" ? CommandRouter.Match(userText) : null;
        // First lessons: Teacher gets to know the student (age → boy/girl → city) before teaching. Body commands still work.
        var onboarding = cutoff is null && opt.Kind is "normal" or "browser_stt" && student.OnboardingStep < Onboarding.Done
                         && command?.Kind is null or "greet" or "call";
        var genderFix = cutoff is null && !onboarding && command is null && opt.Kind is "normal" or "browser_stt"
            ? Onboarding.GenderStatement(userText) : null;
        if (genderFix is not null)
        {
            student.Gender = genderFix;
            await db.SaveChangesAsync(ct);
            replySource = Onboarding.StreamAsync(new Onboarding.Outcome(Onboarding.Done, "fixed",
                    new Onboarding.Facts(null, genderFix, null, null, false)), turnLang, student.DisplayName, student.Gender,
                student.Age, student.City, snapshot.AngerLevel, ct);
        }
        else if (onboarding)
        {
            var facts = Onboarding.Parse(userText, student.OnboardingStep);
            var o = Onboarding.Advance(student.OnboardingStep, facts, student.Age is not null, student.Gender is not null,
                                       student.City is not null, student.OnboardingRetries);
            if (facts.Age is { } fa) student.Age = fa;
            if (facts.Gender is { } fg) student.Gender = fg;
            if (facts.City is { } fc) student.City = fc;
            if (facts.Name is { } fn) student.DisplayName = fn;
            student.OnboardingRetries = o.Kind == "retry" ? student.OnboardingRetries + 1 : 0;
            student.OnboardingStep = o.NextStep;
            await db.SaveChangesAsync(ct);
            replySource = Onboarding.StreamAsync(o, turnLang, student.DisplayName, student.Gender, student.Age, student.City,
                                                 snapshot.AngerLevel, ct);
        }
        else if (command is not null)
            replySource = CommandRouter.StreamAsync(command, turnLang, snapshot.AngerLevel, session.ActiveTopic, ct);
        else if (cutoff is { } reason)
        {
            var recent = await db.Messages.AsNoTracking()
                .Where(m => m.SessionId == session.Id && m.Role == MessageRole.Assistant)
                .OrderByDescending(m => m.Id).Take(3).Select(m => m.Emotion).ToListAsync(ct);
            var repeat = recent.TakeWhile(e => e == "annoyed_cutoff").Count();
            var lastReply = await db.Messages.AsNoTracking()
                .Where(m => m.SessionId == session.Id && m.Role == MessageRole.Assistant)
                .OrderByDescending(m => m.Id).Select(m => m.Content).FirstOrDefaultAsync(ct);
            replySource = CutoffReplies.StreamAsync(reason, userText.StartsWith('(') ? "" : userText, snapshot.Style,
                                                    snapshot.AngerLevel, repeat, session.ActiveTopic, turnLang, ct, lastReply);
        }
        else
        {
            var history = await WithImagesAsync(db, media, await conversations.HistoryForLlmAsync(session.Id, 40, ct), ct);
            var llmContext = new LlmContext(session.Language, session.SessionDate, today, brief.ToString(), snapshot.Style);
            replySource = llm.StreamReplyAsync(history, llmContext, ct);
        }

        // 3) Structured LLM stream → segments → TTS/FX (pipelined, emitted in order) + live board/quiz
        var parser = new StreamingReplyParser();
        var pending = new Queue<(int index, string text, SegmentInfo seg, Task<SpeechChunk> task)>();
        var segments = new List<SpeechSegment>();
        BoardContent? board = null;
        SafetyVerdict? verdict = null;
        Task<ImageJobResult>? imageJob = null;
        ImageRequest? imageReq = null;
        Guid? imageSourceId = null;
        var imagesEmitted = false;
        var generated = new List<MediaAsset>();
        QuizAttempt? newQuiz = null;
        var chunkIndex = 0;
        int? firstAudioMs = null;
        var totalAudio = 0;

        var maskedWords = 0;
        void EnqueueSegment(SpeechSegment s)
        {
            if (s.Type == "speech" && string.IsNullOrWhiteSpace(s.Text)) return;          // nothing to say (e.g. no opener)
            if (s.Type == "speech")
            {
                // Raw-vocabulary guarantee: style + rage + empathy decide what may be SPOKEN (before TTS).
                var h = parser.Header;
                var empathyNow = h is { UserMood: "hurt" or "stressed" } || h?.Emotion == "hurt_apologetic";
                s = s with { Text = InsultFilter.Apply(s.Text, snapshot.Style, empathyNow, h?.RageLevel ?? snapshot.AngerLevel, out var m) };
                maskedWords += m;
            }
            var segIndex = segments.Count;
            segments.Add(s);
            var replyLang = parser.Header?.ReplyLanguage ?? turnLang;
            if (s.Type == "speech")
            {
                var first = true;
                foreach (var sentence in SplitSentences(s.Text))
                {
                    var info = new SegmentInfo(segIndex, s.Type, s.Emotion, s.Intensity, first);
                    pending.Enqueue((chunkIndex++, sentence, info,
                        tts.SynthesizeAsync(sentence, replyLang, VoiceFor(replyLang, pref.Voice), s.Emotion, ct)));
                    first = false;
                }
            }
            else
            {
                var info = new SegmentInfo(segIndex, s.Type, s.Emotion, s.Intensity, true);
                var kind = s.Type.Replace("_sfx", "");                 // laugh | sigh | gasp | scoff | pause
                var dur = kind switch { "laugh" => 700 + 500 * s.Intensity, "sigh" => 1100, "gasp" => 450, "scoff" => 400, _ => 350 * s.Intensity };
                pending.Enqueue((chunkIndex++, s.Text, info, Task.FromResult(new SpeechChunk(null, "sfx/" + kind, dur, []))));
            }
        }

        TurnEvent Emit((int index, string text, SegmentInfo seg, SpeechChunk chunk) x)
        {
            firstAudioMs ??= (int)clock.ElapsedMilliseconds;
            totalAudio += x.chunk.DurationMs;
            return new TurnEvent("speech") { Index = x.index, Text = x.text, Segment = x.seg, Speech = x.chunk };
        }

        await foreach (var delta in replySource)
        {
            var (header, newSegs) = parser.Push(delta);
            if (header is not null) yield return new TurnEvent("mentor") { Mentor = header };
            foreach (var s in newSegs) EnqueueSegment(s);

            // Safety verdict arrives before imageRequest: a flagged turn never reaches the image API.
            verdict ??= parser.TryTakeObject<SafetyVerdict>("safety");
            if (imageJob is null && verdict is { Category: "none" } && parser.TryTakeObject<ImageRequest>("imageRequest") is
                { Action: "edit" or "generate" } ir && !string.IsNullOrWhiteSpace(ir.Prompt))
            {
                imageReq = ir;
                var src = ir.Action == "edit" ? await ResolveSourceAsync(db, attachments, session.Id, ir.SourceImageIndex, ct) : null;
                imageSourceId = src?.Id;
                var bytes = src is null ? null : await media.ReadAsync(src, ct);
                imageJob = RunImageJobAsync(new ImageJob(src is null ? "generate" : "edit", ir.Prompt, Math.Clamp(ir.Count, 1, 4),
                                                         bytes, src?.ContentType), ct);
                yield return new TurnEvent("image_pending") { Index = Math.Clamp(ir.Count, 1, 4), Text = ir.Prompt };
            }

            // Board appears while the voice is still playing (it streams right after the segments).
            if (board is null && parser.TryTakeObject<BoardContent>("boardContent") is { } b)
            {
                board = Sanitize(b);
                if (board.Show) yield return new TurnEvent("board") { Board = board };
            }
            if (newQuiz is null && parser.TryTakeObject<QuizRequest>("quiz") is { Active: true } q
                && !string.IsNullOrWhiteSpace(q.Question) && opt.Kind != "weekly_report")
            {
                newQuiz = await learning.CreateQuizAsync(userId, session.Id, q, ct);
                yield return new TurnEvent("quiz") { Quiz = new QuizPrompt(newQuiz.Id, newQuiz.Question, newQuiz.TimeLimitSeconds) };
            }

            while (pending.TryPeek(out var head) && head.task.IsCompleted)
            {
                pending.Dequeue();
                yield return Emit((head.index, head.text, head.seg, await SafeChunk(head.task, head.index)));
            }
        }

        var reply = parser.Finish();
        if (reply is null && segments.Count == 0)
        {
            log.LogWarning("Unparseable mentor reply: {Raw}", Truncate(parser.Raw, 300));
            EnqueueSegment(new SpeechSegment("speech", "neutral", "Kechirasiz, fikrimni yo'qotib qo'ydim. Savolni yana bir bor ayting.", 1));
        }
        while (pending.TryDequeue(out var item))
            yield return Emit((item.index, item.text, item.seg, await SafeChunk(item.task, item.index)));

        // Image job (ran in parallel with TTS): store results, or hand presets to the browser (offline mode).
        if (imageJob is not null && !imagesEmitted)
        {
            imagesEmitted = true;
            ImageJobResult? result = null;
            string? failure = null;
            try { result = await imageJob.WaitAsync(ImageJobTimeout, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Image job failed");
                failure = "Rasmni o'zgartirib bo'lmadi. Keyinroq yana urinib ko'ramiz.";
            }
            if (failure is not null) yield return TurnEvent.Error(failure);
            else if (result!.ClientPresets is { Count: > 0 } presets && imageSourceId is { } srcId)
                yield return new TurnEvent("image_filters") { Presets = presets, SourceUrl = media.SignedUrl(srcId), Text = imageReq!.Prompt };
            else if (result.Images.Count > 0)
            {
                foreach (var bytes in result.Images)
                    generated.Add(await media.SaveAsync(userId, session.Id, bytes, MediaKind.Generated, imageSourceId, imageReq!.Prompt, ct));
                yield return new TurnEvent("images") { Images = generated.Select(media.Ref).ToList(), Text = imageReq!.Prompt };
            }
            else if (result.ClientPresets is { Count: > 0 })
                yield return TurnEvent.Error("Offline rejimda yangi rasm chizib bo'lmaydi — o'zgartirish uchun rasm yuboring.");
        }

        // 4) Apply actions: anger/homework/progress, mistakes, quiz grading
        var header0 = parser.Header ?? new MentorHeader("neutral", snapshot.AngerLevel, 0, "neutral", [], "");
        if (maskedWords > 0) log.LogInformation("Insult filter masked {N} word(s) (style {Style})", maskedWords, snapshot.Style);
        if (!string.IsNullOrWhiteSpace(header0.PivotToTopic) && header0.PivotToTopic != session.ActiveTopic)
            await db.Sessions.Where(x => x.Id == session.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveTopic, header0.PivotToTopic), CancellationToken.None);
        var finalAnger = snapshot.AngerLevel;
        var policy = await safety.EnforceAsync(userId, session.Id, reply, parser.Header, CancellationToken.None);
        if (policy.Category != "none" || policy.EmpathyMode)
            yield return new TurnEvent("policy") { Policy = policy };
        if (reply is not null)
        {
            finalAnger = await mentor.ApplyAsync(userId, session.Id, today, reply, snapshot.AngerLevel, CancellationToken.None,
                                                 policy.AngerCap);
            if (reply.Mistakes is { Count: > 0 } ms)
            {
                await learning.RecordMistakesAsync(userId, ms, CancellationToken.None);
                yield return new TurnEvent("mistakes") { Mistakes = ms };
            }
        }
        if (quizCtx is not null)
        {
            await learning.RecordQuizOutcomeAsync(quizCtx, userText, reply?.QuizResult, CancellationToken.None);
            yield return new TurnEvent("quiz_result")
            {
                QuizResult = new QuizGrade(true, quizCtx.Quiz.IsCorrect == true,
                    quizCtx.TimedOut ? "Vaqt tugadi" : reply?.QuizResult?.Feedback ?? ""),
            };
        }

        var spoken = string.Join(" ", segments.Select(s => s.Type == "speech" ? s.Text : $"({s.Text})").Where(t => t != "()"));
        var assistantMsg = new ChatMessage
        {
            Role = MessageRole.Assistant, Content = spoken.Trim(), Emotion = header0.Emotion, AngerLevel = finalAnger,
            AudioMs = totalAudio, LatencyMs = firstAudioMs,
            AttachmentUrl = generated.Count > 0 ? MediaStore.CanonicalUrl(generated[0].Id) : null,
            Meta = JsonSerializer.Serialize(new { header0.EyeAndFaceCues, header0.RageLevel, header0.ShyLevel, header0.PivotToTopic,
                                                  segments, board = board is { Show: true } ? board : null },
                                            MentorJson.Options),
        };
        await conversations.AppendAsync(session, assistantMsg, CancellationToken.None);
        if (generated.Count > 0)
        {
            var gids = generated.Select(g => g.Id).ToList();
            await db.Media.Where(m => gids.Contains(m.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.MessageId, assistantMsg.Id), CancellationToken.None);
        }
        var mood = reply?.UserMood ?? header0.UserMood;
        await db.Messages.Where(m => m.Id == userMsg.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.UserMood, mood), CancellationToken.None);

        yield return new TurnEvent("done")
        {
            SessionId = session.Id, LatencyMs = firstAudioMs, RageLevel = finalAnger,
            Text = reply is { AssignHomework.Count: > 0 } ? "homework_assigned" : null,
            Onboarding = student.OnboardingStep < Onboarding.Done ? student.OnboardingStep : null,
        };
    }

    /// <summary>Attach image bytes to the most recent image-bearing user turns; older ones become a placeholder.</summary>
    private static async Task<List<ChatTurn>> WithImagesAsync(AppDbContext db, MediaStore media, List<ChatTurn> history,
                                                              CancellationToken ct)
    {
        var ids = history.Where(h => h.IsUser).Select(h => h.MessageId).ToList();
        var assets = await db.Media.AsNoTracking()
            .Where(m => m.MessageId != null && ids.Contains(m.MessageId.Value) && m.Kind == MediaKind.Upload)
            .OrderBy(m => m.CreatedAt).ToListAsync(ct);
        if (assets.Count == 0) return history;
        var byMsg = assets.GroupBy(a => a.MessageId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var withImages = byMsg.Keys.OrderByDescending(k => k).Take(HistoryImageTurns).ToHashSet();

        var result = new List<ChatTurn>(history.Count);
        foreach (var t in history)
        {
            if (!t.IsUser || !byMsg.TryGetValue(t.MessageId, out var list)) { result.Add(t); continue; }
            if (!withImages.Contains(t.MessageId)) { result.Add(t with { Text = $"{t.Text} [{list.Count} ta rasm biriktirilgan edi]" }); continue; }
            var imgs = new List<ImageInput>();
            foreach (var a in list)
            {
                try { imgs.Add(new ImageInput(a.ContentType, Convert.ToBase64String(await media.ReadAsync(a, ct)))); }
                catch (IOException) { /* file gone → skip */ }
            }
            result.Add(t with { Images = imgs });
        }
        return result;
    }

    /// <summary>Edit source: the indexed attachment of this turn, else the latest upload in this session.</summary>
    private static async Task<MediaAsset?> ResolveSourceAsync(AppDbContext db, List<MediaAsset> attachments, Guid sessionId,
                                                             int index, CancellationToken ct)
    {
        if (attachments.Count > 0) return attachments[Math.Clamp(index, 0, attachments.Count - 1)];
        return await db.Media.AsNoTracking().Where(m => m.SessionId == sessionId && m.Kind == MediaKind.Upload)
            .OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync(ct);
    }

    private Task<ImageJobResult> RunImageJobAsync(ImageJob job, CancellationToken ct) =>
        Task.Run(() => images.RunAsync(job, ct), ct);

    private static string RenderPronunciation(IReadOnlyList<PronouncedWord>? words)
    {
        var weak = words?.Where(w => w.Accuracy < 70).OrderBy(w => w.Accuracy).Take(5).ToList();
        if (weak is not { Count: > 0 }) return "";
        var list = string.Join(", ", weak.Select(w => $"{PromptData.Q(w.Word, 40)} ({w.Accuracy.ToString("0", CultureInfo.InvariantCulture)}/100)"));
        return $"\n## TALAFFUZ (Azure baholashi, 70 dan past)\n{list}\n";
    }

    private static BoardContent Sanitize(BoardContent b) => b with
    {
        Type = Emotions.BoardTypes.Contains(b.Type) ? b.Type : "grammar",
        Title = Truncate(b.Title ?? "", 120),
        MarkdownBody = Truncate(b.MarkdownBody ?? "", 4000),
        HighlightWords = (b.HighlightWords ?? []).Where(w => !string.IsNullOrWhiteSpace(w)).Take(8).ToList(),
    };

    private async Task<SpeechChunk> SafeChunk(Task<SpeechChunk> t, int index)
    {
        try { return await t; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "TTS failed for chunk {Index}", index);
            return new SpeechChunk(null, "text/plain", 0, []);
        }
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var chunker = new SentenceChunker();
        foreach (var s in chunker.Push(text)) yield return s;
        if (chunker.Flush() is { } tail) yield return tail;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    /// <summary>The user's chosen voice if it speaks this language, else our default voice for the language.</summary>
    private static string VoiceFor(string lang, string preferred) =>
        preferred.StartsWith(lang.Split('-')[0], StringComparison.OrdinalIgnoreCase) ? preferred : LanguageSupport.VoiceFor(lang);

    private static int WavMs(byte[] wav) => (int)((wav.Length - 44) / 32.0);
}
