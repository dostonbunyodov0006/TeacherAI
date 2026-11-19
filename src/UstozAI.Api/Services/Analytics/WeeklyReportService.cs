using UstozAI.Api.Services.Mentor;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using UstozAI.Api.Data;
using UstozAI.Api.Services.Conversations;

namespace UstozAI.Api.Services.Analytics;

public record HomeworkStats(int Assigned, int Completed, int Failed, int Pending, int CompletionRate);
public record QuizStats(int Total, int Correct, int TimedOut, double? AvgAnswerSeconds);
public record MistakeStat(string Category, string UserSaid, string CorrectForm, int OccurrenceCount, DateTimeOffset LastSeenAt);
public record DayStat(DateOnly Date, int StudyMinutes, int Questions);

public record WeeklyReportDto(
    DateOnly From, DateOnly To, string TimeZone,
    int StudyMinutes, int PrevStudyMinutes, int ActiveDays, int Questions,
    IReadOnlyList<DayStat> Days, HomeworkStats Homework, QuizStats Quizzes,
    IReadOnlyList<MistakeStat> TopMistakes, int NewMistakes,
    double AvgAnger, int MaxAnger, string? Level, decimal? IeltsEstimate, decimal? TargetIelts);

/// <summary>
/// Last 7 local days (today included) vs the 7 before. Study time = Σ per-session gaps between consecutive
/// messages capped at 5 min (idle time doesn't count) + the last reply's audio length.
/// </summary>
public class WeeklyReportService(AppDbContext db)
{
    private static readonly TimeSpan IdleCap = TimeSpan.FromMinutes(5);

    public async Task<WeeklyReportDto> BuildAsync(Guid userId, string? iana, CancellationToken ct)
    {
        var tzId = iana ?? await db.Preferences.Where(p => p.UserId == userId).Select(p => p.TimeZone).FirstOrDefaultAsync(ct);
        var tz = ConversationService.ResolveZone(tzId);
        var today = ConversationService.LocalToday(tz);
        var from = today.AddDays(-6);
        var prevFrom = from.AddDays(-7);
        DateTimeOffset Utc(DateOnly d) => new(TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(TimeOnly.MinValue), tz), TimeSpan.Zero);
        var (start, end, prevStart) = (Utc(from), Utc(today.AddDays(1)), Utc(prevFrom));

        var msgs = await db.Messages.AsNoTracking()
            .Where(m => m.Session.UserId == userId && m.CreatedAt >= prevStart && m.CreatedAt < end)
            .OrderBy(m => m.SessionId).ThenBy(m => m.Id)
            .Select(m => new { m.SessionId, m.CreatedAt, m.Role, m.AudioMs, m.AngerLevel })
            .ToListAsync(ct);

        // study time per local day
        var perDay = new Dictionary<DateOnly, double>();
        foreach (var g in msgs.GroupBy(m => m.SessionId))
        {
            var list = g.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(list[i].CreatedAt, tz).DateTime);
                var span = i + 1 < list.Count
                    ? TimeSpan.FromTicks(Math.Min((list[i + 1].CreatedAt - list[i].CreatedAt).Ticks, IdleCap.Ticks))
                    : TimeSpan.FromMilliseconds(list[i].AudioMs ?? 0);
                perDay[day] = perDay.GetValueOrDefault(day) + span.TotalMinutes;
            }
        }
        var thisWeek = msgs.Where(m => m.CreatedAt >= start).ToList();
        int Minutes(DateOnly a, DateOnly b) => (int)Math.Round(perDay.Where(kv => kv.Key >= a && kv.Key <= b).Sum(kv => kv.Value));

        var days = Enumerable.Range(0, 7).Select(i => from.AddDays(i)).Select(d => new DayStat(d,
            (int)Math.Round(perDay.GetValueOrDefault(d)),
            thisWeek.Count(m => m.Role == MessageRole.User &&
                                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(m.CreatedAt, tz).DateTime) == d))).ToList();

        // homework assigned this week + anything resolved this week
        var hw = await db.Homework.AsNoTracking()
            .Where(h => h.UserId == userId && (h.CreatedAt >= start || h.ResolvedAt >= start || h.Status == HomeworkStatus.Pending))
            .Select(h => new { h.Status, h.CreatedAt }).ToListAsync(ct);
        var done = hw.Count(h => h.Status == HomeworkStatus.Completed);
        var failed = hw.Count(h => h.Status == HomeworkStatus.Failed);
        var hwStats = new HomeworkStats(hw.Count(h => h.CreatedAt >= start), done, failed,
            hw.Count(h => h.Status == HomeworkStatus.Pending), done + failed == 0 ? 0 : 100 * done / (done + failed));

        var quizzes = await db.Quizzes.AsNoTracking().Where(q => q.UserId == userId && q.AskedAt >= start && q.AskedAt < end)
            .Select(q => new { q.IsCorrect, q.TimedOut, q.AnswerDelayMs }).ToListAsync(ct);
        var answered = quizzes.Where(q => q.AnswerDelayMs is not null && !q.TimedOut).ToList();
        var quizStats = new QuizStats(quizzes.Count, quizzes.Count(q => q.IsCorrect == true), quizzes.Count(q => q.TimedOut),
            answered.Count == 0 ? null : Math.Round(answered.Average(q => q.AnswerDelayMs!.Value) / 1000.0, 1));

        var mistakes = await db.Mistakes.AsNoTracking().Where(m => m.UserId == userId && m.LastSeenAt >= start)
            .OrderByDescending(m => m.OccurrenceCount).Take(5)
            .Select(m => new MistakeStat(m.Category.ToString(), m.UserSaid, m.CorrectForm, m.OccurrenceCount, m.LastSeenAt))
            .ToListAsync(ct);
        var newMistakes = await db.Mistakes.CountAsync(m => m.UserId == userId && m.FirstSeenAt >= start, ct);

        var angers = thisWeek.Where(m => m.AngerLevel is not null).Select(m => m.AngerLevel!.Value).ToList();
        var en = await db.Progress.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId && p.Subject == "english", ct);

        return new WeeklyReportDto(from, today, tz.Id,
            Minutes(from, today), Minutes(prevFrom, from.AddDays(-1)), days.Count(d => d.Questions > 0),
            thisWeek.Count(m => m.Role == MessageRole.User), days, hwStats, quizStats, mistakes, newMistakes,
            angers.Count == 0 ? 0 : Math.Round(angers.Average(), 1), angers.Count == 0 ? 0 : angers.Max(),
            en?.Level, en?.IeltsEstimate, en?.TargetIelts);
    }

    /// <summary>Facts for the mentor's spoken "weekly roast".</summary>
    public static string RenderForMentor(WeeklyReportDto r)
    {
        var inv = CultureInfo.InvariantCulture;
        var b = new StringBuilder("\n## HAFTALIK HISOBOT (bazadan, aniq raqamlar)\n");
        b.AppendLine($"Davr: {r.From:yyyy-MM-dd} — {r.To:yyyy-MM-dd}.");
        b.AppendLine($"O'qish vaqti: {r.StudyMinutes} daqiqa (o'tgan hafta {r.PrevStudyMinutes}). Faol kunlar: {r.ActiveDays}/7. Savollar: {r.Questions}.");
        b.AppendLine($"Uy vazifasi: berilgan {r.Homework.Assigned}, bajarilgan {r.Homework.Completed}, bajarilmagan {r.Homework.Failed}, " +
                     $"kutilmoqda {r.Homework.Pending}, bajarish {r.Homework.CompletionRate}%.");
        b.AppendLine($"Viktorinalar: {r.Quizzes.Total} ta, to'g'ri {r.Quizzes.Correct}, vaqti o'tgan {r.Quizzes.TimedOut}" +
                     (r.Quizzes.AvgAnswerSeconds is { } s ? $", o'rtacha javob {s.ToString("0.0", inv)} s." : "."));
        b.AppendLine($"Yangi xatolar: {r.NewMistakes}. Eng ko'p takrorlanganlar:");
        foreach (var m in r.TopMistakes) b.AppendLine($"- {PromptData.Q(m.UserSaid)} → {PromptData.Q(m.CorrectForm)} ({PromptData.Q(m.Category, 30)}), {m.OccurrenceCount} marta");
        b.AppendLine($"Jahl: o'rtacha {r.AvgAnger.ToString("0.0", inv)}, eng yuqori {r.MaxAnger}. Daraja: {r.Level ?? "noma'lum"}" +
                     (r.IeltsEstimate is { } ie ? $", IELTS taxmini {ie.ToString("0.0", inv)}" : "") +
                     (r.TargetIelts is { } t ? $", maqsad {t.ToString("0.0", inv)}" : "") + ".");
        b.AppendLine("VAZIFA: shu raqamlar asosida haftani o'zingning qattiqqo'l-hazilkash uslubingda ovozli sarhisob qil: " +
                     "yaxshisini maqta, yomonini kulib/urishib ko'rsat (raqamlarni so'z bilan ayt), oxirida keyingi hafta uchun " +
                     "2–3 ta aniq maqsad qo'y. boardContent da qisqa jadval ko'rsat (type grammar, title \"Haftalik hisobot\").");
        return b.ToString();
    }
}
