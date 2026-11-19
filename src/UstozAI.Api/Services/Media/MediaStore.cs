using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UstozAI.Api.Auth;
using UstozAI.Api.Data;

namespace UstozAI.Api.Services.Media;

public class MediaOptions
{
    /// <summary>Where image bytes live. Outside wwwroot — never served statically.</summary>
    public string Root { get; set; } = "App_Data/media";
    public long MaxUploadBytes { get; set; } = 5 * 1024 * 1024;     // Claude's per-image API limit
    public int SignedUrlMinutes { get; set; } = 60;
    public int MaxImagesPerTurn { get; set; } = 4;
}

public record MediaRef(Guid Id, string Url, string Kind, string ContentType);

/// <summary>File storage + signed URLs. Storage paths are derived from the GUID only (no user-controlled names).</summary>
public class MediaStore(AppDbContext db, IOptions<MediaOptions> options, IOptions<JwtOptions> jwt, IWebHostEnvironment env)
{
    private readonly MediaOptions _o = options.Value;
    // Separate key derived from the JWT secret: URL signatures can't be confused with tokens.
    private readonly byte[] _signKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwt.Value.SigningKey),
                                                            Encoding.UTF8.GetBytes("ustoz-media-url-v1"));

    private string RootPath => Path.IsPathRooted(_o.Root) ? _o.Root : Path.Combine(env.ContentRootPath, _o.Root);

    /// <summary>Magic-byte sniffing — the declared Content-Type is not trusted.</summary>
    public static string? SniffImageType(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (b.Length >= 12 && Encoding.ASCII.GetString(b[..4]) == "RIFF" && Encoding.ASCII.GetString(b[8..12]) == "WEBP") return "image/webp";
        if (b.Length >= 6 && (Encoding.ASCII.GetString(b[..6]) is "GIF87a" or "GIF89a")) return "image/gif";
        return null;
    }

    public async Task<MediaAsset> SaveAsync(Guid userId, Guid? sessionId, byte[] bytes, MediaKind kind,
                                            Guid? sourceId = null, string? prompt = null, CancellationToken ct = default)
    {
        var type = SniffImageType(bytes) ?? throw new InvalidDataException("Faqat JPEG, PNG, WebP yoki GIF rasm qabul qilinadi.");
        if (bytes.Length > _o.MaxUploadBytes) throw new InvalidDataException("Rasm juda katta (maksimum 5 MB).");

        var asset = new MediaAsset
        {
            UserId = userId, SessionId = sessionId, Kind = kind, ContentType = type, SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), SourceAssetId = sourceId,
            Prompt = prompt is null ? null : (prompt.Length > 1000 ? prompt[..1000] : prompt),
        };
        asset.StoragePath = Path.Combine(asset.CreatedAt.ToString("yyyy-MM"), $"{asset.Id:N}{Ext(type)}");
        var full = Path.Combine(RootPath, asset.StoragePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, ct);

        db.Media.Add(asset);
        await db.SaveChangesAsync(ct);
        return asset;
    }

    public Task<MediaAsset?> FindOwnedAsync(Guid userId, Guid id, CancellationToken ct) =>
        db.Media.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);

    public string FullPath(MediaAsset a) => Path.Combine(RootPath, a.StoragePath);

    public Task<byte[]> ReadAsync(MediaAsset a, CancellationToken ct) => File.ReadAllBytesAsync(FullPath(a), ct);

    public async Task DeleteAsync(MediaAsset a, CancellationToken ct)
    {
        db.Media.Remove(a);
        await db.SaveChangesAsync(ct);
        try { File.Delete(FullPath(a)); } catch (IOException) { /* best effort */ }
    }

    // ---------- signed URLs: /api/media/{id}?exp=…&sig=… ----------
    public static string CanonicalUrl(Guid id) => $"/api/media/{id}";

    public string SignedUrl(Guid id)
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(_o.SignedUrlMinutes).ToUnixTimeSeconds();
        return $"{CanonicalUrl(id)}?exp={exp}&sig={Sign(id, exp)}";
    }

    public MediaRef Ref(MediaAsset a) => new(a.Id, SignedUrl(a.Id), a.Kind.ToString().ToLowerInvariant(), a.ContentType);

    public bool Verify(Guid id, long exp, string? sig) =>
        sig is not null && exp > DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(id, exp)), Encoding.ASCII.GetBytes(sig));

    private string Sign(Guid id, long exp) =>
        Convert.ToHexString(HMACSHA256.HashData(_signKey, Encoding.UTF8.GetBytes($"{id:N}.{exp}")))[..32].ToLowerInvariant();

    private static string Ext(string type) => type switch
    {
        "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif", _ => ".jpg",
    };
}
