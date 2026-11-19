namespace UstozAI.Api.Data;

public enum MistakeCategory { Grammar = 0, Vocabulary = 1, Pronunciation = 2 }

/// <summary>
/// One row per distinct recurring mistake. The LLM reports mistakes each turn; the server upserts by
/// (user, category, normalized correct form) and increments <see cref="OccurrenceCount"/>.
/// </summary>
public class MistakeLog
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public MistakeCategory Category { get; set; }
    public string UserSaid { get; set; } = "";            // latest wrong form ("I am agree")
    public string CorrectForm { get; set; } = "";         // "I agree"
    public string Key { get; set; } = "";                 // normalized CorrectForm — dedupe key
    public string? Note { get; set; }                     // short rule ("agree is a verb, no 'am'")
    public int OccurrenceCount { get; set; } = 1;
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public User User { get; set; } = null!;
}

/// <summary>A timed pop-quiz. Expected answer never leaves the server.</summary>
public class QuizAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string Question { get; set; } = "";
    public string ExpectedAnswer { get; set; } = "";
    public int TimeLimitSeconds { get; set; } = 10;
    public DateTimeOffset AskedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }        // countdown actually began (client signal)
    public DateTimeOffset? AnsweredAt { get; set; }
    public int? AnswerDelayMs { get; set; }               // countdown start → Space pressed
    public string? StudentAnswer { get; set; }
    public bool? IsCorrect { get; set; }
    public bool TimedOut { get; set; }
    public string? Feedback { get; set; }
    public User User { get; set; } = null!;
    public ConversationSession Session { get; set; } = null!;

    public bool IsOpen => AnsweredAt is null && !TimedOut;
}
