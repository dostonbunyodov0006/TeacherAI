using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using UstozAI.Api.Data;
using UstozAI.Api.Services.Mentor;

namespace UstozAI.Api.Services.Learning;

/// <summary>Per-turn quiz context: the open quiz this turn answers (or times out).</summary>
public record QuizTurnContext(QuizAttempt Quiz, bool TimedOut, int? AnswerDelayMs);

/// <summary>Mistake tracking + pop-quiz lifecycle.</summary>
public partial class LearningService(AppDbContext db)
{
    public const int QuizGraceMs = 1500;          // reaction slack on top of the time limit

    // ---------------- Mistakes ----------------
    public Task<List<MistakeLog>> TopMistakesAsync(Guid userId, int take, CancellationToken ct) =>
        db.Mistakes.AsNoTracking().Where(m => m.UserId == userId)
            .OrderByDescending(m => m.OccurrenceCount).ThenByDescending(m => m.LastSeenAt).Take(take).ToListAsync(ct);

    /// <summary>Upsert by (category, normalized correct form). Returns rows that are now repeats (count ≥ 2).</summary>
    public async Task<List<MistakeLog>> RecordMistakesAsync(Guid userId, IEnumerable<MistakeReport> reports, CancellationToken ct)
    {
        var repeats = new List<MistakeLog>();
        foreach (var r in reports.Take(5))
        {
            if (string.IsNullOrWhiteSpace(r.CorrectForm) || string.IsNullOrWhiteSpace(r.UserSaid)) continue;
            if (!Enum.TryParse<MistakeCategory>(r.Category, true, out var cat)) cat = MistakeCategory.Grammar;
            var key = Normalize(r.CorrectForm);
            if (key.Length == 0 || key == Normalize(r.UserSaid)) continue;          // not actually a mistake

            var row = await db.Mistakes.FirstOrDefaultAsync(m => m.UserId == userId && m.Category == cat && m.Key == key, ct);
            if (row is null)
            {
                row = new MistakeLog { UserId = userId, Category = cat, Key = key };
                db.Mistakes.Add(row);
            }
            else
            {
                row.OccurrenceCount++;
                row.LastSeenAt = DateTimeOffset.UtcNow;
                repeats.Add(row);
            }
            row.UserSaid = Cut(r.UserSaid, 300);
            row.CorrectForm = Cut(r.CorrectForm, 300);
            if (!string.IsNullOrWhiteSpace(r.Note)) row.Note = Cut(r.Note, 500);
        }
        await db.SaveChangesAsync(ct);
        return repeats;
    }

    // ---------------- Quizzes ----------------
    public async Task<QuizAttempt> CreateQuizAsync(Guid userId, Guid sessionId, QuizRequest q, CancellationToken ct)
    {
        // Only one open quiz per session: a new one supersedes the old one (marked timed out).
        await db.Quizzes.Where(x => x.SessionId == sessionId && x.AnsweredAt == null && !x.TimedOut)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TimedOut, true), ct);
        var quiz = new QuizAttempt
        {
            UserId = userId, SessionId = sessionId, Question = Cut(q.Question, 500),
            ExpectedAnswer = Cut(q.ExpectedAnswer, 500), TimeLimitSeconds = Math.Clamp(q.TimeLimitSeconds, 5, 60),
        };
        db.Quizzes.Add(quiz);
        await db.SaveChangesAsync(ct);
        return quiz;
    }

    /// <summary>Client signals the countdown is visible (after Ustoz finished speaking).</summary>
    public async Task<bool> MarkStartedAsync(Guid userId, Guid quizId, CancellationToken ct) =>
        await db.Quizzes.Where(q => q.Id == quizId && q.UserId == userId && q.StartedAt == null && q.AnsweredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.StartedAt, DateTimeOffset.UtcNow), ct) > 0;

    /// <summary>
    /// Resolves the quiz this turn answers. The client reports how long the student took to press Space; the server
    /// caps it by its own clock (StartedAt → now) so a tampered value can't make a late answer look fast.
    /// </summary>
    public async Task<QuizTurnContext?> OpenForAnswerAsync(Guid userId, Guid sessionId, Guid? quizId, int? clientDelayMs,
                                                           bool timeout, CancellationToken ct)
    {
        if (quizId is null) return null;
        var quiz = await db.Quizzes.FirstOrDefaultAsync(q => q.Id == quizId && q.UserId == userId && q.SessionId == sessionId
                                                             && q.AnsweredAt == null && !q.TimedOut, ct);
        if (quiz is null) return null;

        var serverElapsed = quiz.StartedAt is { } st ? (int)(DateTimeOffset.UtcNow - st).TotalMilliseconds : (int?)null;
        var delay = clientDelayMs is { } d && d >= 0
            ? (serverElapsed is { } se ? Math.Min(d, se) : d)
            : serverElapsed;
        var late = timeout || delay > quiz.TimeLimitSeconds * 1000 + QuizGraceMs;
        quiz.AnswerDelayMs = delay;
        quiz.TimedOut = late;
        return new QuizTurnContext(quiz, late, delay);
    }

    public async Task RecordQuizOutcomeAsync(QuizTurnContext ctx, string studentAnswer, QuizGrade? grade, CancellationToken ct)
    {
        var q = ctx.Quiz;
        q.AnsweredAt = DateTimeOffset.UtcNow;
        q.StudentAnswer = ctx.TimedOut && studentAnswer.StartsWith('(') ? null : Cut(studentAnswer, 1000);
        q.IsCorrect = ctx.TimedOut ? false : grade is { Graded: true } ? grade.Correct : null;
        if (grade is { Graded: true } && !string.IsNullOrWhiteSpace(grade.Feedback)) q.Feedback = Cut(grade.Feedback, 1000);
        await db.SaveChangesAsync(ct);
    }

    // ---------------- Brief sections ----------------
    public static string RenderMistakes(IReadOnlyList<MistakeLog> top)
    {
        if (top.Count == 0) return "";
        var b = new StringBuilder("\n## O'QUVCHINING TAKRORIY XATOLARI (bazadan)\n");
        foreach (var m in top)
            b.AppendLine($"- [{m.Category}] \"{m.UserSaid}\" → \"{m.CorrectForm}\" — {m.OccurrenceCount} marta" +
                         (m.Note is null ? "" : $" ({m.Note})"));
        b.AppendLine("Agar o'quvchi shulardan birini yana qilsa: aniq aytib o't (\"Yana o'sha xato! Buni N marta tushuntirdim!\")," +
                     " N = bazadagi son + 1, va mistakes massiviga AYNAN o'sha correctForm bilan yoz.");
        return b.ToString();
    }

    public static string RenderQuiz(QuizTurnContext? ctx)
    {
        if (ctx is null) return "";
        var q = ctx.Quiz;
        var secs = ctx.AnswerDelayMs is { } ms ? $"{ms / 1000.0:0.0} s" : "noma'lum";
        return ctx.TimedOut
            ? $"\n## VIKTORINA NATIJASI\nSavol: \"{q.Question}\" (to'g'ri javob: \"{q.ExpectedAnswer}\", limit {q.TimeLimitSeconds} s).\n" +
              $"O'quvchi VAQTIDA javob bermadi ({secs}). Buni qattiq qayd et, to'g'ri javobni ayt va doskaga yoz. " +
              "quizResult: graded=true, correct=false.\n"
            : $"\n## VIKTORINA JAVOBI\nSavol: \"{q.Question}\". Kutilgan javob: \"{q.ExpectedAnswer}\". Limit {q.TimeLimitSeconds} s, " +
              $"o'quvchi {secs} da javob bera boshladi.\nUshbu foydalanuvchi xabari — shu savolga javob. QAT'IY bahola: " +
              "ma'no va grammatika to'g'ri bo'lishi shart, yarim javob = noto'g'ri. quizResult ni to'ldir (graded=true).\n";
    }

    public static string Normalize(string s) => Spaces().Replace(NonWord().Replace(s.ToLowerInvariant(), " "), " ").Trim();
    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"[^\p{L}\p{N}' ]+")] private static partial Regex NonWord();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
