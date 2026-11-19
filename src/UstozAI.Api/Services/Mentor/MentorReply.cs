using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UstozAI.Api.Services.Mentor;

// ---------------- Structured reply contract (what Claude must return) ----------------

public static class Emotions
{
    /// <summary>Top-level reply emotion (contract v2).</summary>
    public static readonly string[] All =
        ["neutral", "laughing", "mocking", "shy_flustered", "angry_flame", "hurt_apologetic", "thinking", "teaching",
         "annoyed_cutoff"];   // v2.1: reaction to a cut-off / fragmentary utterance
    /// <summary>Per-segment emotion: the 8 above + a few nuance states for single lines.</summary>
    public static readonly string[] Segment = [.. All, "shocked", "encouraging", "curious"];
    public static readonly string[] Cues =
        ["wide_eyes", "squint", "bashful_look", "brow_furrow", "eye_roll", "head_nod", "head_shake", "faint_dramatic",
         "smirk", "slow_blink", "laugh_hard", "apologetic_soft", "curious_inspect", "thinking_look_up", "surprised_brow_raise",
         // v2.1 upper-face detail
         "forehead_crease", "squint_annoyed", "exasperated_roll", "brow_raise", "mouth_smile", "laugh_motion"];
    /// <summary>speech = TTS; *_sfx = real/synth vocalization (never read by TTS); pause = short silence.</summary>
    public static readonly string[] SegmentTypes = ["speech", "laugh_sfx", "sigh_sfx", "gasp_sfx", "scoff_sfx", "pause"];
    public static readonly string[] BoardTypes = ["grammar", "vocabulary", "code", "quiz"];
    public static readonly string[] MistakeCategories = ["grammar", "vocabulary", "pronunciation"];
    /// <summary>The student's mood as read from their words/tone. hurt/stressed → empathy mode (no teasing, no insults).</summary>
    public static readonly string[] Moods = ["neutral", "amused", "hurt", "frustrated", "stressed"];
    public static readonly string[] SafetyCategories = ["none", "sexual", "violence", "self_harm", "drugs", "illegal"];
    public static readonly string[] ImageActions = ["none", "edit", "generate"];
}

/// <summary>Eye/face cue, timed: fires at <c>At</c> (0..1) through speech segment <c>Segment</c>.</summary>
public record FacialCue(string Cue, int Segment, double At);

public record SpeechSegment(string Type, string Emotion, string Text, int Intensity);

public record HomeworkUpdate(string HomeworkId, string Status, string Feedback);   // completed | failed | not_done_yet

public record HomeworkAssignment(string Title, string Description, string Subject, int DueInDays);

public record ProgressUpdate(string Subject, string Level, string Stage, double IeltsEstimate, string Note);

public record TestResultInput(string Subject, string Name, double Score, double MaxScore);

/// <summary>Whiteboard update. <c>Show=false</c> → leave the board as it is.</summary>
public record BoardContent(bool Show, string Type, string Title, string MarkdownBody, IReadOnlyList<string> HighlightWords);

/// <summary>Pop-quiz request: <c>Active=true</c> → the client shows a countdown after the reply is spoken.</summary>
public record QuizRequest(bool Active, string Question, string ExpectedAnswer, int TimeLimitSeconds);

/// <summary>Grading of the answer to the open quiz (only meaningful when the brief says a quiz answer arrived).</summary>
public record QuizGrade(bool Graded, bool Correct, string Feedback);

public record MistakeReport(string Category, string UserSaid, string CorrectForm, string Note);

/// <summary>Hard-boundary classification of the user's request (decided by the model, enforced by the server).</summary>
public record SafetyVerdict(string Category, string Note);

/// <summary>Ask the image pipeline to edit an attached image (sourceImageIndex) or generate a new one.</summary>
public record ImageRequest(string Action, string Prompt, int Count, int SourceImageIndex);

/// <summary>The "header" of a reply — available as soon as speechSegments starts streaming (sent as the "mentor" event).</summary>
/// <summary>Whole-body action (look_right, sit, drink_tea…). BeforeSegment ≥ 0 → the client finishes the action
/// before playing that speech segment (e.g. sip the tea, THEN "let's start the lesson"); -1 → in parallel.</summary>
public record BodyAction(string Action, int BeforeSegment);

public record MentorHeader(string Emotion, int RageLevel, int ShyLevel, string UserMood, IReadOnlyList<FacialCue> EyeAndFaceCues,
                           string PivotToTopic, string? ReplyLanguage = null, BodyAction? BodyAction = null);

public record MentorReply(
    string Emotion, int RageLevel, int ShyLevel, string? UserMood, string? ReplyLanguage, IReadOnlyList<FacialCue> EyeAndFaceCues, string? PivotToTopic,
    BodyAction? BodyAction,
    IReadOnlyList<SpeechSegment> SpeechSegments,
    SafetyVerdict? Safety, ImageRequest? ImageRequest,
    BoardContent? BoardContent, QuizRequest? Quiz, QuizGrade? QuizResult, IReadOnlyList<MistakeReport>? Mistakes,
    IReadOnlyList<HomeworkUpdate> HomeworkUpdates, IReadOnlyList<HomeworkAssignment> AssignHomework,
    IReadOnlyList<ProgressUpdate> ProgressUpdates, IReadOnlyList<TestResultInput> TestResults);

public static class MentorJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// JSON Schema for structured outputs. Property ORDER matters: header → segments → actions, so the
    /// incremental parser can start TTS on segment 1 while the model is still writing the rest.
    /// </summary>
    public static Dictionary<string, JsonElement> Schema()
    {
        object Str() => new { type = "string" };
        object Int() => new { type = "integer" };
        object Num() => new { type = "number" };
        object Enum(string[] v) => new { type = "string", @enum = v };
        object Obj(object props, string[] req) => new { type = "object", properties = props, required = req, additionalProperties = false };
        object Arr(object items) => new { type = "array", items };

        var properties = new Dictionary<string, object>
        {
            // --- header (contract v2): everything before speechSegments is sent to the client immediately
            ["emotion"] = Enum(Emotions.All),
            ["rageLevel"] = Int(),
            ["shyLevel"] = Int(),
            ["userMood"] = Enum(Emotions.Moods),
            ["replyLanguage"] = Enum(UstozAI.Api.Services.Speech.LanguageSupport.All),   // the language the student spoke
            ["eyeAndFaceCues"] = Arr(Obj(new { cue = Enum(Emotions.Cues), segment = Int(), at = Num() },
                                         ["cue", "segment", "at"])),
            ["pivotToTopic"] = Str(),
            ["bodyAction"] = Obj(new { action = Enum(UstozAI.Api.Services.Pipeline.BodyActions.All), beforeSegment = Int() },
                                 ["action", "beforeSegment"]),
            ["speechSegments"] = Arr(Obj(new { type = Enum(Emotions.SegmentTypes), emotion = Enum(Emotions.Segment), text = Str(), intensity = Int() },
                                         ["type", "emotion", "text", "intensity"])),
            // --- after the segments, so they never delay the first audio; the parser emits them as soon as each closes
            // safety BEFORE imageRequest: the server must see the verdict before it starts any image job.
            ["safety"] = Obj(new { category = Enum(Emotions.SafetyCategories), note = Str() }, ["category", "note"]),
            ["imageRequest"] = Obj(new { action = Enum(Emotions.ImageActions), prompt = Str(), count = Int(), sourceImageIndex = Int() },
                                   ["action", "prompt", "count", "sourceImageIndex"]),
            ["boardContent"] = Obj(new { show = new { type = "boolean" }, type = Enum(Emotions.BoardTypes), title = Str(),
                                         markdownBody = Str(), highlightWords = Arr(Str()) },
                                   ["show", "type", "title", "markdownBody", "highlightWords"]),
            ["quiz"] = Obj(new { active = new { type = "boolean" }, question = Str(), expectedAnswer = Str(), timeLimitSeconds = Int() },
                           ["active", "question", "expectedAnswer", "timeLimitSeconds"]),
            ["quizResult"] = Obj(new { graded = new { type = "boolean" }, correct = new { type = "boolean" }, feedback = Str() },
                                 ["graded", "correct", "feedback"]),
            ["mistakes"] = Arr(Obj(new { category = Enum(Emotions.MistakeCategories), userSaid = Str(), correctForm = Str(), note = Str() },
                                   ["category", "userSaid", "correctForm", "note"])),
            ["homeworkUpdates"] = Arr(Obj(new { homeworkId = Str(), status = Enum(["completed", "failed", "not_done_yet"]), feedback = Str() },
                                          ["homeworkId", "status", "feedback"])),
            ["assignHomework"] = Arr(Obj(new { title = Str(), description = Str(), subject = Str(), dueInDays = Int() },
                                         ["title", "description", "subject", "dueInDays"])),
            ["progressUpdates"] = Arr(Obj(new { subject = Str(), level = Str(), stage = Str(), ieltsEstimate = Num(), note = Str() },
                                          ["subject", "level", "stage", "ieltsEstimate", "note"])),
            ["testResults"] = Arr(Obj(new { subject = Str(), name = Str(), score = Num(), maxScore = Num() },
                                      ["subject", "name", "score", "maxScore"])),
        };
        var schema = new
        {
            type = "object",
            properties,
            required = properties.Keys.ToArray(),
            additionalProperties = false,
        };
        var root = JsonSerializer.SerializeToElement(schema);
        return root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}

/// <summary>
/// Incremental parser for the streamed JSON reply. Emits the header once, then each speech segment the
/// moment its closing brace arrives. String-aware brace matching — no full-document parse until the end.
/// </summary>
public class StreamingReplyParser
{
    private readonly StringBuilder _buf = new();
    private int _arrayStart = -1;     // index just after '[' of speechSegments
    private int _scan;                // next index to scan inside the array
    private bool _arrayDone;
    private readonly HashSet<string> _emittedObjects = [];
    public MentorHeader? Header { get; private set; }
    public int SegmentsEmitted { get; private set; }

    public string Raw => _buf.ToString();

    /// <returns>(header if it just became available, newly completed segments)</returns>
    public (MentorHeader? header, List<SpeechSegment> segments) Push(string delta)
    {
        _buf.Append(delta);
        MentorHeader? newHeader = null;
        var segs = new List<SpeechSegment>();
        var text = _buf.ToString();

        if (_arrayStart < 0)
        {
            var key = text.IndexOf("\"speechSegments\"", StringComparison.Ordinal);
            if (key < 0) return (null, segs);
            var bracket = text.IndexOf('[', key);
            if (bracket < 0) return (null, segs);
            _arrayStart = _scan = bracket + 1;
            Header = newHeader = ParseHeader(text[..key]);
        }

        while (!_arrayDone)
        {
            // skip whitespace and commas between objects
            while (_scan < text.Length && (char.IsWhiteSpace(text[_scan]) || text[_scan] == ',')) _scan++;
            if (_scan >= text.Length) break;
            if (text[_scan] == ']') { _arrayDone = true; break; }
            if (text[_scan] != '{') { _scan++; continue; }
            var end = MatchObject(text, _scan);
            if (end < 0) break;                               // object not complete yet
            var seg = JsonSerializer.Deserialize<SpeechSegment>(text.AsSpan(_scan, end - _scan + 1), MentorJson.Options);
            _scan = end + 1;
            if (seg is not null) { segs.Add(Normalize(seg)); SegmentsEmitted++; }
        }
        return (newHeader, segs);
    }

    /// <summary>
    /// After the segments: returns a top-level object (e.g. "boardContent", "quiz") the first time it is complete,
    /// so the board / quiz can appear while the voice is still playing. Null if not (yet) available.
    /// </summary>
    public T? TryTakeObject<T>(string name) where T : class
    {
        if (!_arrayDone || _emittedObjects.Contains(name)) return null;
        var text = _buf.ToString();
        var key = text.IndexOf($"\"{name}\"", _scan, StringComparison.Ordinal);
        if (key < 0) return null;
        var open = text.IndexOf('{', key);
        if (open < 0) return null;
        var end = MatchObject(text, open);
        if (end < 0) return null;
        _emittedObjects.Add(name);
        try { return JsonSerializer.Deserialize<T>(text.AsSpan(open, end - open + 1), MentorJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Full parse at end of stream (actions live after the segments).</summary>
    public MentorReply? Finish()
    {
        var text = _buf.ToString();
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');            // tolerate ```json … ``` around the object
        if (a < 0 || b <= a) return null;
        try
        {
            var r = JsonSerializer.Deserialize<MentorReply>(text.AsSpan(a, b - a + 1), MentorJson.Options);
            // Smaller models may leave out empty fields — never let a missing list crash the turn.
            return r is null ? null : r with
            {
                Emotion = string.IsNullOrWhiteSpace(r.Emotion) ? "neutral" : r.Emotion,
                EyeAndFaceCues = r.EyeAndFaceCues ?? [], SpeechSegments = r.SpeechSegments ?? [],
                HomeworkUpdates = r.HomeworkUpdates ?? [], AssignHomework = r.AssignHomework ?? [],
                ProgressUpdates = r.ProgressUpdates ?? [], TestResults = r.TestResults ?? [],
            };
        }
        catch (JsonException) { return null; }
    }

    private static MentorHeader ParseHeader(string prefix)
    {
        // prefix = '{ "emotion": …, "rageLevel": …, "shyLevel": …, "userMood": …, "eyeAndFaceCues": […], "pivotToTopic": "…",' → close + parse
        var brace = prefix.IndexOf('{');                    // tolerate a ```json fence before the object
        if (brace > 0) prefix = prefix[brace..];
        var json = prefix.TrimEnd().TrimEnd(',') + "}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Str(string k, string d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? d : d;
            int Int(string k) => r.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? Math.Clamp(i, 0, 10) : 0;
            var emotion = Str("emotion", "neutral");
            var mood = Str("userMood", "neutral");
            var cues = r.TryGetProperty("eyeAndFaceCues", out var c) ? c.Deserialize<List<FacialCue>>(MentorJson.Options) ?? [] : [];
            var pivot = Str("pivotToTopic", "");
            var lang = Str("replyLanguage", "");
            BodyAction? body = null;
            if (r.TryGetProperty("bodyAction", out var ba) && ba.ValueKind == JsonValueKind.Object)
            {
                var act = ba.TryGetProperty("action", out var av) ? av.GetString() ?? "none" : "none";
                var before = ba.TryGetProperty("beforeSegment", out var bv) && bv.TryGetInt32(out var bi) ? bi : -1;
                if (act != "none" && UstozAI.Api.Services.Pipeline.BodyActions.All.Contains(act)) body = new BodyAction(act, Math.Clamp(before, -1, 20));
            }
            return new MentorHeader(Emotions.All.Contains(emotion) ? emotion : "neutral", Int("rageLevel"), Int("shyLevel"),
                Emotions.Moods.Contains(mood) ? mood : "neutral",
                cues.Where(x => Emotions.Cues.Contains(x.Cue)).ToList(), pivot.Length > 120 ? pivot[..120] : pivot,
                UstozAI.Api.Services.Speech.LanguageSupport.IsSupported(lang) ? lang : null, body);
        }
        catch (JsonException) { return new MentorHeader("neutral", 0, 0, "neutral", [], "", null); }
    }

    private static int MatchObject(string s, int start)
    {
        var depth = 0; var inStr = false;
        for (var i = start; i < s.Length; i++)
        {
            var ch = s[i];
            if (inStr)
            {
                if (ch == '\\') i++;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') inStr = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static SpeechSegment Normalize(SpeechSegment s) => s with
    {
        Type = Emotions.SegmentTypes.Contains(s.Type) ? s.Type
             : s.Type is "laugh" or "sigh" or "gasp" or "scoff" ? s.Type + "_sfx" : "speech",   // v1 names still accepted
        Emotion = Emotions.Segment.Contains(s.Emotion) ? s.Emotion : "neutral",
        Intensity = Math.Clamp(s.Intensity, 1, 3),
        Text = s.Text ?? "",
    };
}
