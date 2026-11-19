namespace UstozAI.Api.Data;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";          // stored lower-case, unique
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";      // ASP.NET Core Identity PBKDF2 (v3) hash
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // "Teacher o'zi tanishadi" — filled by voice on the first lesson (Onboarding.cs)
    public string? Gender { get; set; }                  // male | female
    public int? Age { get; set; }
    public string? City { get; set; }
    public int OnboardingStep { get; set; }              // 0 start … 4 done
    public int OnboardingRetries { get; set; }

    public UserPreference? Preference { get; set; }
    public List<ConversationSession> Sessions { get; set; } = [];
    public List<RefreshToken> RefreshTokens { get; set; } = [];
}

/// <summary>1:1 with User. Language/voice/avatar choices.</summary>
public class UserPreference
{
    public Guid UserId { get; set; }
    public string Language { get; set; } = "uz-UZ";     // BCP-47; drives STT, LLM reply language and TTS
    public string Voice { get; set; } = "uz-UZ-SardorNeural";
    public string AvatarId { get; set; } = "ustoz-classic";
    public double SpeechRate { get; set; } = 1.0;
    /// <summary>IANA zone reported by the browser (e.g. "Asia/Tashkent"). Defines the user's calendar day.</summary>
    public string TimeZone { get; set; } = "Asia/Tashkent";
    /// <summary>"soft" = polite tutor; "strict" = blunt, sarcastic, may swear when provoked (anger >= 8).</summary>
    public string MentorStyle { get; set; } = "strict";
    public User User { get; set; } = null!;
}

/// <summary>Rotating refresh token. Only a SHA-256 hash is stored, never the raw token.</summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
    public User User { get; set; } = null!;

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}

/// <summary>
/// One conversation per user per local calendar day ("daily thread"). Past days can be reopened and
/// continued; new messages always attach to the session the client has selected.
/// </summary>
public class ConversationSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    /// <summary>User-local calendar date (in <see cref="UserPreference.TimeZone"/> at creation time). Unique per user.</summary>
    public DateOnly SessionDate { get; set; }
    public string Title { get; set; } = "";              // topic preview: first user question of the day
    public string Language { get; set; } = "uz-UZ";
    public int MessageCount { get; set; }                // denormalized for a fast sidebar (no COUNT(*) per row)
    /// <summary>The lesson/task currently being worked on (the mentor's pivotToTopic). Lets the next turn continue it.</summary>
    public string? ActiveTopic { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public User User { get; set; } = null!;
    public List<ChatMessage> Messages { get; set; } = [];
}

public enum MessageRole { User = 0, Assistant = 1 }

public class ChatMessage
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = "";
    public string? Emotion { get; set; }                 // avatar state used for this reply (encouraging/explaining…)
    public int? AudioMs { get; set; }                    // user audio length or TTS length
    public int? LatencyMs { get; set; }                  // release-Space → first audio (assistant rows)
    public int? AngerLevel { get; set; }                 // assistant rows: mentor anger after this reply
    /// <summary>jsonb: structured reply (segments + facial cues) so history can be replayed with expressions.</summary>
    public string? Meta { get; set; }
    /// <summary>Canonical (unsigned) URL of the first attached image: "/api/media/{id}". All attachments: MediaAsset.MessageId.</summary>
    public string? AttachmentUrl { get; set; }
    /// <summary>User rows: mood the mentor detected (neutral/amused/hurt/frustrated/stressed).</summary>
    public string? UserMood { get; set; }
    public List<MediaAsset> Attachments { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ConversationSession Session { get; set; } = null!;
}
