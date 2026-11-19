using Microsoft.EntityFrameworkCore;
using UstozAI.Api.Auth;
using UstozAI.Api.Data;
using UstozAI.Api.Services.Conversations;

namespace UstozAI.Api.Endpoints;

public record PreferenceDto(string Language, string? Voice, string? AvatarId, double SpeechRate);
public record RenameRequest(string? Title);

public static class ConversationEndpoints
{
    /// <summary>The browser sends its IANA zone (Intl…resolvedOptions().timeZone) on every request.</summary>
    public const string TimeZoneHeader = "X-Time-Zone";
    private static readonly HashSet<string> Languages = ["uz-UZ", "ru-RU", "en-US", "tr-TR"];

    private static string? Tz(HttpContext http) => http.Request.Headers[TimeZoneHeader].FirstOrDefault();

    public static void MapConversations(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/conversations").RequireAuthorization();

        // Sidebar: grouped by day, newest first. Paging: ?before=yyyy-MM-dd (use NextBefore from the last page).
        g.MapGet("/", async (DateOnly? before, HttpContext http, ConversationService svc, CancellationToken ct) =>
            await svc.ListAsync(http.User.UserId(), Tz(http), before, ct));

        // "Yangi chat": a fresh conversation (an empty one of today is reused).
        g.MapPost("/", async (HttpContext http, ConversationService svc, CancellationToken ct) =>
        {
            var uid = http.User.UserId();
            var s = await svc.CreateNewAsync(uid, Tz(http), ct);
            return await svc.GetDetailAsync(uid, s.Id, Tz(http), ct);
        });

        g.MapPatch("/{sessionId:guid}", async (Guid sessionId, RenameRequest req, HttpContext http, ConversationService svc,
                                                CancellationToken ct) =>
            await svc.RenameAsync(http.User.UserId(), sessionId, req.Title ?? "", ct) ? Results.NoContent() : Results.BadRequest());

        // Get or create today's thread in the user's local time zone (idempotent).
        g.MapPost("/today", async (HttpContext http, ConversationService svc, CancellationToken ct) =>
        {
            var uid = http.User.UserId();
            var s = await svc.GetOrCreateTodayAsync(uid, Tz(http), ct);
            return await svc.GetDetailAsync(uid, s.Id, Tz(http), ct);
        });

        // Full history of one day (user transcripts + Ustoz replies + emotion/latency metadata).
        g.MapGet("/{sessionId:guid}/messages", async (Guid sessionId, HttpContext http, ConversationService svc,
                                                       CancellationToken ct) =>
            await svc.GetDetailAsync(http.User.UserId(), sessionId, Tz(http), ct) is { } d
                ? Results.Ok(d) : Results.NotFound());

        g.MapDelete("/{sessionId:guid}", async (Guid sessionId, HttpContext http, ConversationService svc,
                                                CancellationToken ct) =>
            await svc.DeleteAsync(http.User.UserId(), sessionId, ct) ? Results.NoContent() : Results.NotFound());

        app.MapPut("/api/preferences", async (PreferenceDto dto, HttpContext http, AppDbContext db) =>
        {
            if (!Languages.Contains(dto.Language)) return Results.BadRequest("Unsupported language");
            var uid = http.User.UserId();
            var p = await db.Preferences.FindAsync(uid);
            if (p is null) { p = new UserPreference { UserId = uid }; db.Preferences.Add(p); }
            p.Language = dto.Language;
            if (!string.IsNullOrWhiteSpace(dto.Voice)) p.Voice = dto.Voice[..Math.Min(dto.Voice.Length, 64)];          // (review #14)
            if (!string.IsNullOrWhiteSpace(dto.AvatarId)) p.AvatarId = dto.AvatarId[..Math.Min(dto.AvatarId.Length, 64)];
            p.SpeechRate = Math.Clamp(dto.SpeechRate, 0.5, 2.0);
            // A thread keeps the language it was started in unless it's still empty.
            await db.Sessions.Where(s => s.UserId == uid && s.MessageCount == 0)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Language, dto.Language));
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
