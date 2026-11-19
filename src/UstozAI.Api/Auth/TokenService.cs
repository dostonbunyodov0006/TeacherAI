using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using UstozAI.Api.Data;

namespace UstozAI.Api.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "UstozAI";
    public string Audience { get; set; } = "UstozAI.Web";
    /// <summary>HMAC-SHA256 key, min 32 bytes. Set via env Jwt__SigningKey in production.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;

    public SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _o = options.Value;

    public (string token, DateTimeOffset expires) CreateAccessToken(User user)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(_o.AccessTokenMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim("name", user.DisplayName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        var jwt = new JwtSecurityToken(_o.Issuer, _o.Audience, claims,
            notBefore: DateTime.UtcNow, expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(_o.Key(), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }

    /// <summary>Returns the raw token (sent to client in an HttpOnly cookie) and its hash (stored in DB).</summary>
    public (string raw, string hash, DateTimeOffset expires) CreateRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        return (raw, Hash(raw), DateTimeOffset.UtcNow.AddDays(_o.RefreshTokenDays));
    }

    public static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}

public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) =>
        Guid.Parse(p.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? p.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
