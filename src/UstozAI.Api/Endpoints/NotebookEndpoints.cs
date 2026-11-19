using Microsoft.EntityFrameworkCore;
using UstozAI.Api.Auth;
using UstozAI.Api.Data;

namespace UstozAI.Api.Endpoints;

public record NotebookListItem(Guid Id, string Title, DateTimeOffset UpdatedAt);
public record NotebookDto(Guid Id, string Title, string Content, DateTimeOffset UpdatedAt);
public record SaveNotebookRequest(string? Title, string? Content);

public static class NotebookEndpoints
{
    public static void MapNotebooks(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notebooks").RequireAuthorization();

        g.MapGet("/", async (HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var uid = http.User.UserId();
            return await db.Notebooks.AsNoTracking().Where(n => n.UserId == uid).OrderByDescending(n => n.UpdatedAt).Take(200)
                .Select(n => new NotebookListItem(n.Id, n.Title, n.UpdatedAt)).ToListAsync(ct);
        });

        g.MapPost("/", async (HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var n = new Notebook { UserId = http.User.UserId() };
            db.Notebooks.Add(n);
            await db.SaveChangesAsync(ct);
            return new NotebookDto(n.Id, n.Title, n.Content, n.UpdatedAt);
        });

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var uid = http.User.UserId();
            var n = await db.Notebooks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct);
            return n is null ? Results.NotFound() : Results.Ok(new NotebookDto(n.Id, n.Title, n.Content, n.UpdatedAt));
        });

        g.MapPut("/{id:guid}", async (Guid id, SaveNotebookRequest req, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var uid = http.User.UserId();
            var n = await db.Notebooks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct);
            if (n is null) return Results.NotFound();
            if (req.Title is { } t) n.Title = string.IsNullOrWhiteSpace(t) ? "Nomsiz bloknot" : t.Trim()[..Math.Min(t.Trim().Length, 200)];
            if (req.Content is { } c)
            {
                if (c.Length > 100_000) return Results.BadRequest("Bloknot juda katta (100 000 belgigacha).");
                n.Content = c;
            }
            n.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new NotebookDto(n.Id, n.Title, n.Content, n.UpdatedAt));
        });

        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var uid = http.User.UserId();
            return await db.Notebooks.Where(x => x.Id == id && x.UserId == uid).ExecuteDeleteAsync(ct) > 0
                ? Results.NoContent() : Results.NotFound();
        });
    }
}
