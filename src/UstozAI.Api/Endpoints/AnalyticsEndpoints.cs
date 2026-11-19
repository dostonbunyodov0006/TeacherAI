using Microsoft.EntityFrameworkCore;
using UstozAI.Api.Auth;
using UstozAI.Api.Data;
using UstozAI.Api.Services.Analytics;

namespace UstozAI.Api.Endpoints;

public record MistakeDto(long Id, string Category, string UserSaid, string CorrectForm, string? Note, int OccurrenceCount,
                         DateTimeOffset LastSeenAt);

public static class AnalyticsEndpoints
{
    public static void MapAnalytics(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // Numbers only; the spoken roast is the hub method WeeklyRoast (same service, persona on top).
        g.MapGet("/analytics/weekly-report", async (HttpContext http, WeeklyReportService svc, CancellationToken ct) =>
            await svc.BuildAsync(http.User.UserId(), http.Request.Headers[ConversationEndpoints.TimeZoneHeader].FirstOrDefault(), ct));

        g.MapGet("/mistakes", async (string? category, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var uid = http.User.UserId();
            var q = db.Mistakes.AsNoTracking().Where(m => m.UserId == uid);
            if (Enum.TryParse<MistakeCategory>(category, true, out var cat)) q = q.Where(m => m.Category == cat);
            return await q.OrderByDescending(m => m.OccurrenceCount).ThenByDescending(m => m.LastSeenAt).Take(100)
                .Select(m => new MistakeDto(m.Id, m.Category.ToString(), m.UserSaid, m.CorrectForm, m.Note, m.OccurrenceCount, m.LastSeenAt))
                .ToListAsync(ct);
        });
    }
}
