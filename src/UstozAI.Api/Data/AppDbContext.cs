using Microsoft.EntityFrameworkCore;

namespace UstozAI.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserPreference> Preferences => Set<UserPreference>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ConversationSession> Sessions => Set<ConversationSession>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<StudentProgress> Progress => Set<StudentProgress>();
    public DbSet<StudentTestResult> TestResults => Set<StudentTestResult>();
    public DbSet<HomeworkTask> Homework => Set<HomeworkTask>();
    public DbSet<MentorState> MentorStates => Set<MentorState>();
    public DbSet<MistakeLog> Mistakes => Set<MistakeLog>();
    public DbSet<QuizAttempt> Quizzes => Set<QuizAttempt>();
    public DbSet<MediaAsset> Media => Set<MediaAsset>();
    public DbSet<SafetyEvent> SafetyEvents => Set<SafetyEvent>();
    public DbSet<Notebook> Notebooks => Set<Notebook>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(x => x.Gender).HasMaxLength(8);
            e.Property(x => x.City).HasMaxLength(64);
        });

        b.Entity<UserPreference>(e =>
        {
            e.ToTable("user_preferences");
            e.HasKey(x => x.UserId);
            e.HasOne(x => x.User).WithOne(u => u.Preference)
             .HasForeignKey<UserPreference>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Language).HasMaxLength(16);
            e.Property(x => x.Voice).HasMaxLength(64);
            e.Property(x => x.AvatarId).HasMaxLength(64);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.MentorStyle).HasMaxLength(16).HasDefaultValue("strict");
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(128);
            e.Property(x => x.ReplacedByHash).HasMaxLength(128);
            e.HasOne(x => x.User).WithMany(u => u.RefreshTokens)
             .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.IsActive);
        });

        b.Entity<ConversationSession>(e =>
        {
            e.ToTable("conversation_sessions");
            // One thread per user per day; also the sidebar's index (scan backwards by date).
            // several chats per day are allowed ("Yangi chat"); indexes serve the sidebar and "today's newest"
            e.HasIndex(x => new { x.UserId, x.SessionDate }).IsDescending(false, true);
            e.HasIndex(x => new { x.UserId, x.LastMessageAt });
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(16);
            e.Property(x => x.ActiveTopic).HasMaxLength(120);
            e.HasOne(x => x.User).WithMany(u => u.Sessions)
             .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.HasIndex(x => new { x.SessionId, x.Id });
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Emotion).HasMaxLength(32);
            e.Property(x => x.Meta).HasColumnType("jsonb");
            e.Property(x => x.AttachmentUrl).HasMaxLength(100);
            e.Property(x => x.UserMood).HasMaxLength(16);
            e.HasOne(x => x.Session).WithMany(s => s.Messages)
             .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StudentProgress>(e =>
        {
            e.ToTable("student_progress");
            e.HasIndex(x => new { x.UserId, x.Subject }).IsUnique();
            e.Property(x => x.Subject).HasMaxLength(32);
            e.Property(x => x.Level).HasMaxLength(16);
            e.Property(x => x.Stage).HasMaxLength(200);
            e.Property(x => x.IeltsEstimate).HasPrecision(3, 1);
            e.Property(x => x.TargetIelts).HasPrecision(3, 1);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StudentTestResult>(e =>
        {
            e.ToTable("student_test_results");
            e.HasIndex(x => new { x.UserId, x.Subject, x.TakenAt });
            e.Property(x => x.Subject).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Score).HasPrecision(6, 2);
            e.Property(x => x.MaxScore).HasPrecision(6, 2);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<HomeworkTask>(e =>
        {
            e.ToTable("homework_tasks");
            e.HasIndex(x => new { x.UserId, x.Status, x.DueDate });   // "pending homework" lookup every turn
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Subject).HasMaxLength(32);
            e.Property(x => x.TeacherFeedback).HasMaxLength(2000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MentorState>(e =>
        {
            e.ToTable("mentor_states");
            e.HasKey(x => x.UserId);
            e.HasOne(x => x.User).WithOne().HasForeignKey<MentorState>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MistakeLog>(e =>
        {
            e.ToTable("mistake_logs");
            e.HasIndex(x => new { x.UserId, x.Category, x.Key }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.OccurrenceCount });
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.UserSaid).HasMaxLength(300);
            e.Property(x => x.CorrectForm).HasMaxLength(300);
            e.Property(x => x.Key).HasMaxLength(300);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<QuizAttempt>(e =>
        {
            e.ToTable("quiz_attempts");
            e.HasIndex(x => new { x.SessionId, x.AskedAt });
            e.HasIndex(x => new { x.UserId, x.AskedAt });
            e.Property(x => x.Question).HasMaxLength(500);
            e.Property(x => x.ExpectedAnswer).HasMaxLength(500);
            e.Property(x => x.StudentAnswer).HasMaxLength(1000);
            e.Property(x => x.Feedback).HasMaxLength(1000);
            e.Ignore(x => x.IsOpen);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MediaAsset>(e =>
        {
            e.ToTable("media_assets");
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => x.MessageId);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ContentType).HasMaxLength(32);
            e.Property(x => x.StoragePath).HasMaxLength(200);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Prompt).HasMaxLength(1000);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Message).WithMany(m => m.Attachments).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<SafetyEvent>(e =>
        {
            e.ToTable("safety_events");
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.Category).HasMaxLength(16);
            e.Property(x => x.Note).HasMaxLength(300);
        });

        b.Entity<Notebook>(e =>
        {
            e.ToTable("notebooks");
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Content).HasMaxLength(100_000);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
