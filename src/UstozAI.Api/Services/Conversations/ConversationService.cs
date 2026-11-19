using Microsoft.EntityFrameworkCore;
using UstozAI.Api.Data;

namespace UstozAI.Api.Services.Conversations;

public record ConversationItemDto(Guid Id, DateOnly Date, string Title, string Language, int MessageCount,
                                  DateTimeOffset? LastMessageAt, bool IsToday);
/// <summary>Key: "today" | "yesterday" | "last7" | "last30" | "yyyy-MM" (older, by month). Client localizes labels.</summary>
public record ConversationGroupDto(string Key, IReadOnlyList<ConversationItemDto> Items);
public record ConversationListDto(DateOnly Today, string TimeZone, IReadOnlyList<ConversationGroupDto> Groups,
                                  DateOnly? NextBefore);
public record MessageDto(long Id, string Role, string Content, string? Emotion, int? AngerLevel, int? LatencyMs, DateTimeOffset CreatedAt,
                         string? Meta = null, IReadOnlyList<UstozAI.Api.Services.Media.MediaRef>? Attachments = null);
public record ConversationDetailDto(ConversationItemDto Conversation, IReadOnlyList<MessageDto> Messages);

/// <summary>All daily-thread logic in one place: user-local "today", get-or-create, sidebar listing, history.</summary>
public class ConversationService(AppDbContext db, UstozAI.Api.Services.Media.MediaStore media)
{
    public const int SidebarPageSize = 60;

    // ---------- Time zone ----------
    public static TimeZoneInfo ResolveZone(string? iana)
    {
        if (!string.IsNullOrWhiteSpace(iana))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }      // .NET 6+: IANA ids work on Windows via ICU
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    public static DateOnly LocalToday(TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);

    /// <summary>Stores the browser's IANA zone if it is valid and changed. Returns the effective zone.</summary>
    public async Task<TimeZoneInfo> SyncTimeZoneAsync(Guid userId, string? iana, CancellationToken ct)
    {
        var pref = await db.Preferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (pref is null) { pref = new UserPreference { UserId = userId }; db.Preferences.Add(pref); }
        var valid = !string.IsNullOrWhiteSpace(iana) && iana.Length <= 64
                    && (iana is "UTC" or "Etc/UTC" || ResolveZone(iana) != TimeZoneInfo.Utc);
        if (valid && iana is not null && iana != pref.TimeZone)
        {
            pref.TimeZone = iana;
            await db.SaveChangesAsync(ct);
        }
        return ResolveZone(pref.TimeZone);
    }

    // ---------- Get or create today's thread (race-safe via unique index) ----------
    /// <summary>The user's most recent chat of today (local day), created if there is none yet.</summary>
    public async Task<ConversationSession> GetOrCreateTodayAsync(Guid userId, string? iana, CancellationToken ct)
    {
        var tz = await SyncTimeZoneAsync(userId, iana, ct);
        var today = LocalToday(tz);
        var existing = await db.Sessions.Where(s => s.UserId == userId && s.SessionDate == today)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        return existing ?? await CreateAsync(userId, today, ct);
    }

    /// <summary>
    /// "Yangi chat": a fresh, empty conversation. An already-empty chat of today is reused, so pressing the
    /// button repeatedly doesn't litter the list with blank chats (same as Gemini / ChatGPT).
    /// </summary>
    public async Task<ConversationSession> CreateNewAsync(Guid userId, string? iana, CancellationToken ct)
    {
        var tz = await SyncTimeZoneAsync(userId, iana, ct);
        var today = LocalToday(tz);
        var empty = await db.Sessions.Where(s => s.UserId == userId && s.SessionDate == today && s.MessageCount == 0)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        return empty ?? await CreateAsync(userId, today, ct);
    }

    private async Task<ConversationSession> CreateAsync(Guid userId, DateOnly day, CancellationToken ct)
    {
        var lang = await db.Preferences.Where(p => p.UserId == userId).Select(p => p.Language).FirstOrDefaultAsync(ct)
                   ?? "uz-UZ";
        var created = new ConversationSession { UserId = userId, SessionDate = day, Language = lang };
        db.Sessions.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }

    public async Task<bool> RenameAsync(Guid userId, Guid sessionId, string title, CancellationToken ct)
    {
        title = title.Trim();
        if (title.Length == 0) return false;
        if (title.Length > 200) title = title[..200];
        return await db.Sessions.Where(s => s.Id == sessionId && s.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Title, title), ct) > 0;
    }

    /// <summary>Session owned by the user, or null (never leaks other users' rows).</summary>
    public Task<ConversationSession?> FindOwnedAsync(Guid userId, Guid sessionId, CancellationToken ct) =>
        db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);

    // ---------- Sidebar ----------
    /// <summary>
    /// Sidebar: every chat, most recently active first (flat list like Gemini). Empty chats are hidden except
    /// today's newest one (the "Yangi chat" you just opened). Groups are kept in the DTO for older clients.
    /// </summary>
    public async Task<ConversationListDto> ListAsync(Guid userId, string? iana, DateOnly? before, CancellationToken ct)
    {
        var tz = await SyncTimeZoneAsync(userId, iana, ct);
        var today = LocalToday(tz);

        var q = db.Sessions.AsNoTracking().Where(s => s.UserId == userId && (s.MessageCount > 0 || s.SessionDate == today));
        if (before is { } b) q = q.Where(s => s.SessionDate < b);

        var rows = await q.OrderByDescending(s => s.LastMessageAt ?? s.CreatedAt).Take(SidebarPageSize + 1)
            .Select(s => new { s.Id, s.SessionDate, s.Title, s.Language, s.MessageCount, s.LastMessageAt, s.CreatedAt })
            .ToListAsync(ct);

        // at most one empty chat in the list (the newest)
        var firstEmpty = rows.FirstOrDefault(r => r.MessageCount == 0)?.Id;
        rows = rows.Where(r => r.MessageCount > 0 || r.Id == firstEmpty).ToList();

        var hasMore = rows.Count > SidebarPageSize;
        var page = rows.Take(SidebarPageSize)
            .Select(r => new ConversationItemDto(r.Id, r.SessionDate, r.Title, r.Language, r.MessageCount,
                                                 r.LastMessageAt ?? r.CreatedAt, r.SessionDate == today))
            .ToList();
        var groups = new List<ConversationGroupDto> { new("all", page) };
        return new ConversationListDto(today, tz.Id, groups, hasMore ? page[^1].Date : null);
    }

    public static string GroupKey(DateOnly d, DateOnly today)
    {
        var days = today.DayNumber - d.DayNumber;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 7 => "last7",
            < 30 => "last30",
            _ => d.ToString("yyyy-MM"),
        };
    }

    // ---------- History ----------
    public async Task<ConversationDetailDto?> GetDetailAsync(Guid userId, Guid sessionId, string? iana, CancellationToken ct)
    {
        var s = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, ct);
        if (s is null) return null;
        var today = LocalToday(ResolveZone(iana ?? await ZoneOf(userId, ct)));
        var msgs = await db.Messages.AsNoTracking().Where(m => m.SessionId == sessionId).OrderBy(m => m.Id)
            .Select(m => new MessageDto(m.Id, m.Role == MessageRole.User ? "user" : "assistant", m.Content, m.Emotion,
                                        m.AngerLevel, m.LatencyMs, m.CreatedAt, m.Meta, null))
            .ToListAsync(ct);
        // attachments (uploads + generated images) with fresh signed URLs
        var ids = msgs.Select(m => m.Id).ToList();
        var assets = await db.Media.AsNoTracking().Where(x => x.MessageId != null && ids.Contains(x.MessageId.Value))
            .OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var byMsg = assets.GroupBy(x => x.MessageId!.Value).ToDictionary(g => g.Key, g => g.Select(media.Ref).ToList());
        msgs = msgs.Select(m => byMsg.TryGetValue(m.Id, out var list) ? m with { Attachments = list } : m).ToList();
        return new ConversationDetailDto(
            new ConversationItemDto(s.Id, s.SessionDate, s.Title, s.Language, s.MessageCount, s.LastMessageAt,
                                    s.SessionDate == today), msgs);
    }

    /// <summary>LLM context: the last <paramref name="max"/> turns of this session, oldest first.</summary>
    public async Task<List<ChatTurn>> HistoryForLlmAsync(Guid sessionId, int max, CancellationToken ct) =>
        await db.Messages.AsNoTracking().Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Id).Take(max).OrderBy(m => m.Id)
            .Select(m => new ChatTurn(m.Role == MessageRole.User, m.Content, null, m.Id)).ToListAsync(ct);

    /// <summary>Appends a message and keeps the sidebar's denormalized fields in sync (same transaction).</summary>
    public async Task AppendAsync(ConversationSession session, ChatMessage msg, CancellationToken ct)
    {
        msg.SessionId = session.Id;
        db.Messages.Add(msg);
        if (session.MessageCount == 0 && msg.Role == MessageRole.User && string.IsNullOrEmpty(session.Title))
            session.Title = msg.Content.Length > 80 ? msg.Content[..80].TrimEnd() + "…" : msg.Content;
        session.MessageCount++;
        session.LastMessageAt = msg.CreatedAt;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid sessionId, CancellationToken ct) =>
        await db.Sessions.Where(s => s.Id == sessionId && s.UserId == userId).ExecuteDeleteAsync(ct) > 0;

    private Task<string?> ZoneOf(Guid userId, CancellationToken ct) =>
        db.Preferences.Where(p => p.UserId == userId).Select(p => p.TimeZone).FirstOrDefaultAsync(ct);
}
