namespace UstozAI.Api.Data;

/// <summary>"Bloknot" — the student's own notes page (sidebar → Bloknotlar → Yangi bloknot).</summary>
public class Notebook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Title { get; set; } = "Yangi bloknot";
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public User User { get; set; } = null!;
}
