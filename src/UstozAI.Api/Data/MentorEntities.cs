namespace UstozAI.Api.Data;

/// <summary>The student's REAL level per subject. The mentor compares every claim against this.</summary>
public class StudentProgress
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Subject { get; set; } = "english";     // lower-case key: english, math, ...
    public string? Level { get; set; }                    // e.g. CEFR "A2", "B1"; null = not assessed yet
    public string? Stage { get; set; }                    // current topic: "Present Perfect", "IELTS Writing Task 1"
    public decimal? IeltsEstimate { get; set; }           // realistic band estimate (english only)
    public decimal? TargetIelts { get; set; }             // what the student says they want
    public string? Notes { get; set; }                    // mentor's running notes: weak spots, habits
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public User User { get; set; } = null!;
}

public class StudentTestResult
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string Subject { get; set; } = "english";
    public string Name { get; set; } = "";                // "Mini-test: irregular verbs"
    public decimal Score { get; set; }
    public decimal MaxScore { get; set; }
    public DateTimeOffset TakenAt { get; set; } = DateTimeOffset.UtcNow;
    public User User { get; set; } = null!;
}

public enum HomeworkStatus { Pending = 0, Completed = 1, Failed = 2 }

public class HomeworkTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? SessionId { get; set; }                  // the lesson (day) it was assigned in
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Subject { get; set; } = "english";
    public DateOnly DueDate { get; set; }
    public HomeworkStatus Status { get; set; } = HomeworkStatus.Pending;
    public string? TeacherFeedback { get; set; }
    /// <summary>How many times the student was asked and admitted it wasn't done. Drives the scolding level.</summary>
    public int MissedCheckins { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public User User { get; set; } = null!;
    public ConversationSession? Session { get; set; }
}

/// <summary>Mentor's persistent mood toward this student. Anger cools down over time.</summary>
public class MentorState
{
    public Guid UserId { get; set; }
    public int AngerLevel { get; set; }                   // 0..10
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public User User { get; set; } = null!;
}
