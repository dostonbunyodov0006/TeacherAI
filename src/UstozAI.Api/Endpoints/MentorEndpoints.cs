using Microsoft.EntityFrameworkCore;
using UstozAI.Api.Auth;
using UstozAI.Api.Data;
using UstozAI.Api.Services.Mentor;

namespace UstozAI.Api.Endpoints;

public record ProgressDto(string Subject, string? Level, string? Stage, decimal? IeltsEstimate, decimal? TargetIelts,
                          string? Notes, DateTimeOffset UpdatedAt);
public record HomeworkDto(Guid Id, string Title, string Description, string Subject, DateOnly DueDate, string Status,
                          string? TeacherFeedback, int MissedCheckins);
public record MentorProfileDto(int AngerLevel, string Style, IReadOnlyList<ProgressDto> Progress,
                               IReadOnlyList<HomeworkDto> Homework, int CompletionRate);
public record SetProgressRequest(string? Level, string? Stage, decimal? TargetIelts);
public record SetStyleRequest(string Style);

public static class MentorEndpoints
{
    private static readonly HashSet<string> Levels = ["A1", "A2", "B1", "B2", "C1", "C2"];

    public static void MapMentor(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/mentor").RequireAuthorization();

        // Profile panel: real level, homework (pending first), anger, completion rate.
        g.MapGet("/", async (HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var uid = http.User.UserId();
            var state = await db.MentorStates.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == uid, ct);
            var style = await db.Preferences.Where(p => p.UserId == uid).Select(p => p.MentorStyle).FirstOrDefaultAsync(ct) ?? "strict";
            var progress = await db.Progress.AsNoTracking().Where(p => p.UserId == uid).OrderBy(p => p.Subject)
                .Select(p => new ProgressDto(p.Subject, p.Level, p.Stage, p.IeltsEstimate, p.TargetIelts, p.Notes, p.UpdatedAt))
                .ToListAsync(ct);
            var hw = await db.Homework.AsNoTracking().Where(h => h.UserId == uid)
                .OrderBy(h => h.Status).ThenByDescending(h => h.CreatedAt).Take(30)
                .Select(h => new HomeworkDto(h.Id, h.Title, h.Description, h.Subject, h.DueDate, h.Status.ToString(),
                                             h.TeacherFeedback, h.MissedCheckins))
                .ToListAsync(ct);
            var done = hw.Count(h => h.Status == "Completed");
            var resolved = hw.Count(h => h.Status != "Pending");
            return new MentorProfileDto(MentorStateService.EffectiveAnger(state), style, progress, hw,
                resolved == 0 ? 100 : 100 * done / resolved);
        });

        // Student/admin can set the starting level (the mentor refines it via progressUpdates).
        g.MapPut("/progress/{subject}", async (string subject, SetProgressRequest req, HttpContext http, AppDbContext db,
                                               CancellationToken ct) =>
        {
            subject = subject.Trim().ToLowerInvariant();
            if (subject.Length is 0 or > 32) return Results.BadRequest("subject");
            if (req.Level is not null && !Levels.Contains(req.Level)) return Results.BadRequest("level must be A1..C2");
            if (req.TargetIelts is < 0 or > 9) return Results.BadRequest("targetIelts 0..9");
            var uid = http.User.UserId();
            var row = await db.Progress.FirstOrDefaultAsync(p => p.UserId == uid && p.Subject == subject, ct);
            if (row is null) { row = new StudentProgress { UserId = uid, Subject = subject }; db.Progress.Add(row); }
            if (req.Level is not null) row.Level = req.Level;
            if (req.Stage is not null) row.Stage = req.Stage.Length > 200 ? req.Stage[..200] : req.Stage;
            if (req.TargetIelts is not null) row.TargetIelts = req.TargetIelts;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPut("/style", async (SetStyleRequest req, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            if (!MentorPersona.Styles.Contains(req.Style)) return Results.BadRequest("style must be soft|strict|savage");
            var uid = http.User.UserId();
            var p = await db.Preferences.FindAsync([uid], ct);
            if (p is null) { p = new UserPreference { UserId = uid }; db.Preferences.Add(p); }
            p.MentorStyle = req.Style;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
