using Microsoft.AspNetCore.Mvc;
using UstozAI.Api.Auth;
using UstozAI.Api.Data;
using UstozAI.Api.Services.Conversations;
using UstozAI.Api.Services.Media;

namespace UstozAI.Api.Endpoints;

public static class MediaEndpoints
{
    public static void MapMedia(this IEndpointRouteBuilder app)
    {
        // Upload: multipart/form-data, field "file". The browser already downsized + re-encoded (EXIF stripped).
        app.MapPost("/api/media", async (HttpContext http, [FromForm] IFormFile file, [FromForm] Guid? sessionId,
                                         MediaStore store, ConversationService conversations, CancellationToken ct) =>
            {
                if (file.Length is 0 or > 5 * 1024 * 1024) return Results.BadRequest("Rasm 0–5 MB bo'lishi kerak.");
                // The chat must belong to the uploader. 404 (not 403): don't even confirm that someone else's chat exists.
                if (sessionId is { } sid && await conversations.FindOwnedAsync(http.User.UserId(), sid, ct) is null)
                    return Results.NotFound();
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                try
                {
                    var asset = await store.SaveAsync(http.User.UserId(), sessionId, ms.ToArray(), MediaKind.Upload, ct: ct);
                    return Results.Ok(store.Ref(asset));
                }
                catch (InvalidDataException ex) { return Results.BadRequest(ex.Message); }
            })
            .RequireAuthorization()
            .RequireRateLimiting("upload")
            .DisableAntiforgery()                          // JWT bearer API, no cookies → no CSRF surface
            .WithMetadata(new RequestSizeLimitAttribute(6 * 1024 * 1024));

        // Serve: no bearer header possible from <img>, so access is by short-lived HMAC signature instead.
        app.MapGet("/api/media/{id:guid}", async (Guid id, long exp, string? sig, MediaStore store, AppDbContext db,
                                                  HttpContext http, CancellationToken ct) =>
        {
            if (!store.Verify(id, exp, sig)) return Results.StatusCode(403);
            var asset = await db.Media.FindAsync([id], ct);
            if (asset is null || !File.Exists(store.FullPath(asset))) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=3600";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'";
            return Results.File(store.FullPath(asset), asset.ContentType, enableRangeProcessing: false);
        });

        app.MapDelete("/api/media/{id:guid}", async (Guid id, HttpContext http, MediaStore store, CancellationToken ct) =>
        {
            var asset = await store.FindOwnedAsync(http.User.UserId(), id, ct);
            if (asset is null) return Results.NotFound();
            await store.DeleteAsync(asset, ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
