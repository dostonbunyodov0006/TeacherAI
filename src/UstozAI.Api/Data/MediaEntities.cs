namespace UstozAI.Api.Data;

public enum MediaKind { Upload = 0, Generated = 1 }

/// <summary>
/// An image the user uploaded or Ustoz generated/edited. Bytes live on disk (App_Data/media, outside wwwroot);
/// served only through short-lived signed URLs.
/// </summary>
public class MediaAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? SessionId { get; set; }
    public long? MessageId { get; set; }                 // the chat message it belongs to (set when the turn is saved)
    public MediaKind Kind { get; set; }
    public string ContentType { get; set; } = "image/jpeg";
    public string StoragePath { get; set; } = "";         // relative to the media root; derived from Id only
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public Guid? SourceAssetId { get; set; }             // generated: which upload it was derived from
    public string? Prompt { get; set; }                  // generated: the edit/generation prompt
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public User User { get; set; } = null!;
    public ChatMessage? Message { get; set; }
}

/// <summary>Audit trail of hard-boundary hits (category + short note only — no message content is copied here).</summary>
public class SafetyEvent
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? SessionId { get; set; }
    public string Category { get; set; } = "";           // sexual | violence | self_harm | drugs | illegal
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
