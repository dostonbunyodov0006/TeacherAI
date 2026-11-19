using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using UstozAI.Api.Data;

namespace UstozAI.Api.Services.Mentor;

public record HomeworkBrief(Guid Id, string Title, string Subject, DateOnly DueDate, int MissedCheckins, bool Overdue);
public record MentorSnapshot(
    int AngerLevel, string Style, bool IsSessionStart,
    IReadOnlyList<StudentProgress> Progress, IReadOnlyList<HomeworkBrief> Pending,
    int Completed, int Failed, IReadOnlyList<StudentTestResult> RecentTests);

/// <summary>
/// The mentor's memory: real skill level, homework accountability and a persistent anger level.
/// Builds the per-turn context block for the LLM and applies the actions the LLM returns (validated).
/// </summary>
public class MentorStateService(AppDbContext db, ILogger<MentorStateService> log)
{
    private const int CoolDownHoursPerPoint = 3;    // anger drops 1 point per 3 h without new provocation
    private const int MaxAngerStepPerTurn = 4;      // no 0 → 10 jumps on a single sentence
    private static readonly TimeSpan SessionGap = TimeSpan.FromHours(3);

    public async Task<MentorSnapshot> SnapshotAsync(Guid userId, ConversationSession session, DateOnly today,
                                                    CancellationToken ct)
    {
        var state = await db.MentorStates.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var style = await db.Preferences.Where(p => p.UserId == userId).Select(p => p.MentorStyle)
                        .FirstOrDefaultAsync(ct) ?? "strict";
        var progress = await db.Progress.AsNoTracking().Where(p => p.UserId == userId).OrderBy(p => p.Subject).ToListAsync(ct);
        var pending = await db.Homework.AsNoTracking()
            .Where(h => h.UserId == userId && h.Status == HomeworkStatus.Pending)
            .OrderBy(h => h.DueDate).Take(10)
            .Select(h => new HomeworkBrief(h.Id, h.Title, h.Subject, h.DueDate, h.MissedCheckins, h.DueDate < today))
            .ToListAsync(ct);
        var counts = await db.Homework.Where(h => h.UserId == userId && h.Status != HomeworkStatus.Pending)
            .GroupBy(h => h.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var tests = await db.TestResults.AsNoTracking().Where(t => t.UserId == userId)
            .OrderByDescending(t => t.TakenAt).Take(5).ToListAsync(ct);

        // "Session start" = first exchange of this day's thread, or the student came back after a long gap.
        // (Snapshot is taken BEFORE the new user message is saved.)
        var isStart = session.MessageCount == 0 ||
                      session.LastMessageAt is { } last && DateTimeOffset.UtcNow - last > SessionGap;

        return new MentorSnapshot(EffectiveAnger(state), style, isStart, progress, pending,
            counts.FirstOrDefault(c => c.Key == HomeworkStatus.Completed)?.N ?? 0,
            counts.FirstOrDefault(c => c.Key == HomeworkStatus.Failed)?.N ?? 0, tests);
    }

    public static int EffectiveAnger(MentorState? s)
    {
        if (s is null) return 0;
        var cooled = (int)((DateTimeOffset.UtcNow - s.UpdatedAt).TotalHours / CoolDownHoursPerPoint);
        return Math.Clamp(s.AngerLevel - cooled, 0, 10);
    }

    /// <summary>Facts block injected into the system prompt every turn. Plain, verifiable data only.</summary>
    /// <summary>What "A1 … C2" concretely means for how Teacher talks and teaches.</summary>
    public static string? LevelGuide(string? level) => level?.ToUpperInvariant() switch
    {
        "A1" => "faqat eng oddiy so'zlar (top-500), 3–6 so'zli gaplar, sekin; tushuntirish asosan o'zbekchada, inglizcha misol + tarjima; bitta yangi so'z/qoida bir martada.",
        "A2" => "kundalik so'zlar, qisqa gaplar (Present/Past Simple), tushuntirish yarmi o'zbekcha; har misolga tarjima.",
        "B1" => "o'rtacha so'z boyligi, Present Perfect / Future / modal fe'llar; tushuntirish asosan inglizcha, qiyin joyda o'zbekcha izoh.",
        "B2" => "erkin suhbat, murakkab gaplar, phrasal verbs, conditionals; deyarli faqat inglizcha; IELTS 5.5–6.5 uslubidagi savollar.",
        "C1" => "ilg'or lug'at, idiomalar, collocations, nuance; faqat inglizcha; IELTS 7+ Speaking/Writing talablari bilan baholash.",
        "C2" => "ona tili darajasi: nozik ma'no farqlari, uslub (formal/informal), akademik va badiiy til; faqat inglizcha, tez sur'at.",
        _ => null,
    };

    public static string RenderBrief(MentorSnapshot s, DateOnly today)
    {
        var inv = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        b.AppendLine("## O'QUVCHI HAQIDA HAQIQIY MA'LUMOT (bazadan, ishonchli)");
        b.AppendLine($"Bugun: {today:yyyy-MM-dd}. Mentor uslubi: {s.Style}. Hozirgi jahl darajang (rageLevel): {s.AngerLevel}/10.");

        if (s.Progress.Count == 0)
            b.AppendLine("Daraja: hali aniqlanmagan. Gap orasida 2–3 ta qisqa savol bilan darajasini tekshirib ko'r va progressUpdates orqali yoz.");
        foreach (var p in s.Progress)
        {
            b.Append($"- {p.Subject}: daraja {p.Level ?? "noma'lum"}");
            if (p.Stage is not null) b.Append($", hozirgi mavzu: {p.Stage}");
            if (p.IeltsEstimate is { } ie) b.Append($", real IELTS taxmini: {ie.ToString("0.0", inv)}");
            if (p.TargetIelts is { } t) b.Append($", o'quvchi maqsadi: {t.ToString("0.0", inv)}");
            if (!string.IsNullOrWhiteSpace(p.Notes)) b.Append($". Izohlaring: {p.Notes}");
            b.AppendLine();
            if (LevelGuide(p.Level) is { } g) b.AppendLine($"  → {p.Subject} {p.Level} darajasiga MOSLASH (majburiy): {g}");
        }
        b.AppendLine($"USLUB ({s.Style}) — MAJBURIY: " + s.Style switch
        {
            "soft" => "mehribon, sabrli, ko'p maqtov; xatoni yumshoq tuzat; hech qachon baqirma; rageLevel 3 dan oshmasin.",
            "savage" => "olovli, keskin, kinoyali, filtrsiz (ruxsat etilgan doirada); har xato uchun achchiq tanbeh, lekin dars mazmuni aniq.",
            _ => "qattiqqo'l, talabchan, qisqa va aniq; dangasalikni kechirma, lekin haqorat qilma.",
        });
        var total = s.Completed + s.Failed;
        b.AppendLine(total == 0
            ? "Uy vazifalari tarixi: hali yo'q."
            : $"Uy vazifasi bajarish ko'rsatkichi: {s.Completed}/{total} ({100 * s.Completed / total}%), bajarilmagan: {s.Failed}.");
        foreach (var t in s.RecentTests)
            b.AppendLine($"- Test \"{t.Name}\" ({t.Subject}): {t.Score.ToString("0.#", inv)}/{t.MaxScore.ToString("0.#", inv)}, {t.TakenAt:yyyy-MM-dd}");

        if (s.Pending.Count == 0) b.AppendLine("Kutilayotgan uy vazifasi yo'q.");
        else
        {
            b.AppendLine("Kutilayotgan uy vazifalari (homeworkId bilan):");
            foreach (var h in s.Pending)
                b.AppendLine($"- [{h.Id}] \"{h.Title}\" ({h.Subject}), muddat {h.DueDate:yyyy-MM-dd}" +
                             (h.Overdue ? " — MUDDATI O'TGAN" : "") +
                             (h.MissedCheckins > 0 ? $", allaqachon {h.MissedCheckins} marta qilinmagan" : ""));
        }
        if (s.IsSessionStart && s.Pending.Count > 0)
            b.AppendLine("DIQQAT: bu dars boshlanishi. Birinchi navbatda uy vazifasi haqida so'ra.");
        return b.ToString();
    }

    /// <summary>Persist anger + apply homework/progress/test actions. Every id is checked against the user.</summary>
    /// <param name="angerCap">From <see cref="SafetyPolicy"/>: empathy mode caps anger (never scold someone who's hurting).</param>
    public async Task<int> ApplyAsync(Guid userId, Guid sessionId, DateOnly today, MentorReply reply, int previousAnger,
                                      CancellationToken ct, int angerCap = 10)
    {
        var anger = Math.Clamp(reply.RageLevel, previousAnger - MaxAngerStepPerTurn, previousAnger + MaxAngerStepPerTurn);
        anger = Math.Clamp(anger, 0, Math.Clamp(angerCap, 0, 10));

        var state = await db.MentorStates.FindAsync([userId], ct);
        if (state is null) { state = new MentorState { UserId = userId }; db.MentorStates.Add(state); }
        state.AngerLevel = anger;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        foreach (var u in reply.HomeworkUpdates)
        {
            if (!Guid.TryParse(u.HomeworkId, out var hid)) continue;
            var hw = await db.Homework.FirstOrDefaultAsync(h => h.Id == hid && h.UserId == userId, ct);
            if (hw is null || hw.Status != HomeworkStatus.Pending) continue;
            switch (u.Status)
            {
                case "completed":
                    hw.Status = HomeworkStatus.Completed; hw.ResolvedAt = DateTimeOffset.UtcNow; break;
                case "failed":
                    hw.Status = HomeworkStatus.Failed; hw.ResolvedAt = DateTimeOffset.UtcNow; hw.MissedCheckins++; break;
                case "not_done_yet":
                    hw.MissedCheckins++; break;
            }
            if (!string.IsNullOrWhiteSpace(u.Feedback)) hw.TeacherFeedback = Trim(u.Feedback, 2000);
        }

        // Max 3 new tasks per turn; skip duplicates of a still-pending task.
        foreach (var a in reply.AssignHomework.Take(3))
        {
            if (string.IsNullOrWhiteSpace(a.Title)) continue;
            var title = Trim(a.Title, 200);
            if (await db.Homework.AnyAsync(h => h.UserId == userId && h.Status == HomeworkStatus.Pending && h.Title == title, ct))
                continue;
            db.Homework.Add(new HomeworkTask
            {
                UserId = userId, SessionId = sessionId, Title = title, Description = Trim(a.Description, 2000),
                Subject = Subject(a.Subject), DueDate = today.AddDays(Math.Clamp(a.DueInDays, 1, 14)),
            });
        }

        foreach (var p in reply.ProgressUpdates.Take(3))
        {
            var subject = Subject(p.Subject);
            var row = await db.Progress.FirstOrDefaultAsync(x => x.UserId == userId && x.Subject == subject, ct);
            if (row is null) { row = new StudentProgress { UserId = userId, Subject = subject }; db.Progress.Add(row); }
            if (!string.IsNullOrWhiteSpace(p.Level)) row.Level = Trim(p.Level, 16);
            if (!string.IsNullOrWhiteSpace(p.Stage)) row.Stage = Trim(p.Stage, 200);
            if (p.IeltsEstimate is > 0 and <= 9) row.IeltsEstimate = Math.Round((decimal)p.IeltsEstimate * 2) / 2;
            if (!string.IsNullOrWhiteSpace(p.Note))
                row.Notes = Trim(string.IsNullOrEmpty(row.Notes) ? p.Note : $"{row.Notes}\n{p.Note}", 2000, keepEnd: true);
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        foreach (var t in reply.TestResults.Take(3))
        {
            if (t.MaxScore <= 0 || t.Score < 0 || t.Score > t.MaxScore) continue;
            db.TestResults.Add(new StudentTestResult
            {
                UserId = userId, Subject = Subject(t.Subject), Name = Trim(t.Name, 200),
                Score = (decimal)t.Score, MaxScore = (decimal)t.MaxScore,
            });
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Mentor state for {User}: anger {Anger}, +{Hw} homework, {Upd} updates", userId, anger,
            reply.AssignHomework.Count, reply.HomeworkUpdates.Count);
        return anger;
    }

    private static string Subject(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "english" : Trim(s.Trim().ToLowerInvariant(), 32);

    private static string Trim(string s, int max, bool keepEnd = false) =>
        s.Length <= max ? s : keepEnd ? s[^max..] : s[..max];
}
