using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UstozAI.Api.Data;

namespace UstozAI.Api.Auth;

public record LoginRequest(string Username, string Password);
/// <param name="Website">Honeypot: hidden from people, bots fill it in. Must stay empty.</param>
public record RegisterRequest(string? Name, string? Username, string? Password, string? Website = null);
public record ProfileRequest(string? Gender);
public record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, UserDto User);
public record UserDto(Guid Id, string Username, string DisplayName, string Language, string Voice,
                      string? Gender = null, int? Age = null, string? City = null, int OnboardingStep = 0);

public static class AuthEndpoints
{
    private const string RefreshCookie = "ustoz_rt";
    private const int MaxFailedLogins = 5;

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").RequireRateLimiting("auth");

        g.MapPost("/login", async (LoginRequest req, AppDbContext db, IPasswordHasher<User> hasher,
                                   TokenService tokens, HttpContext http) =>
        {
            var username = (req.Username ?? "").Trim().ToLowerInvariant();
            var user = await db.Users.Include(u => u.Preference).FirstOrDefaultAsync(u => u.Username == username);

            // Same generic error for "no such user" and "wrong password" — no user enumeration.
            if (user is null) return Results.Problem("Login yoki parol noto'g'ri.", statusCode: 401);
            if (user.LockoutUntil > DateTimeOffset.UtcNow)
                return Results.Problem("Juda ko'p urinish. Birozdan keyin qayta urinib ko'ring.", statusCode: 429);

            var verdict = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "");
            if (verdict == PasswordVerificationResult.Failed)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= MaxFailedLogins)
                {
                    user.LockoutUntil = DateTimeOffset.UtcNow.AddMinutes(5);
                    user.FailedLoginCount = 0;
                }
                await db.SaveChangesAsync();
                return Results.Problem("Login yoki parol noto'g'ri.", statusCode: 401);
            }
            if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
                user.PasswordHash = hasher.HashPassword(user, req.Password!);

            user.FailedLoginCount = 0;
            user.LockoutUntil = null;
            return await IssueAsync(user, db, tokens, http);
        });

        // Ro'yxatdan o'tish: only name + login + password. The rest Teacher asks by voice on the first lesson.
        g.MapPost("/register", async (RegisterRequest req, AppDbContext db, IPasswordHasher<User> hasher,
                                      TokenService tokens, HttpContext http) =>
        {
            // A bot filled the invisible field → refuse without saying why.
            if (!string.IsNullOrEmpty(req.Website)) return Results.Problem("Ro'yxatdan o'tib bo'lmadi. Keyinroq urinib ko'ring.", statusCode: 400);
            var name = System.Text.RegularExpressions.Regex.Replace((req.Name ?? "").Trim(), @"\s+", " ");
            var username = (req.Username ?? "").Trim().ToLowerInvariant();
            var password = req.Password ?? "";
            if (name.Length is < 2 or > 40) return Results.Problem("Ismingizni yozing (2–40 harf).", statusCode: 400);
            if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-z0-9_.]{3,32}$"))
                return Results.Problem("Login 3–32 belgi: lotin harflari, raqam, _ yoki . bo'lsin.", statusCode: 400);
            if (password.Length < 6) return Results.Problem("Parol kamida 6 belgidan iborat bo'lsin.", statusCode: 400);
            if (await db.Users.AnyAsync(u => u.Username == username))
                return Results.Problem("Bu login band. Boshqasini tanlang.", statusCode: 409);

            var user = new User { Username = username, DisplayName = name, Preference = new UserPreference() };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Problem("Bu login band. Boshqasini tanlang.", statusCode: 409); }
            return await IssueAsync(user, db, tokens, http);
        }).RequireRateLimiting("register");

        g.MapPost("/refresh", async (AppDbContext db, TokenService tokens, HttpContext http) =>
        {
            if (!http.Request.Cookies.TryGetValue(RefreshCookie, out var raw)) return Results.Unauthorized();
            var hash = TokenService.Hash(raw);
            var rt = await db.RefreshTokens.Include(t => t.User).ThenInclude(u => u.Preference)
                                           .FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (rt is null) return Results.Unauthorized();

            if (!rt.IsActive)
            {
                // Reused within 30 s of its own rotation = two tabs/requests refreshing at once (benign): just 401.
                if (rt.ReplacedByHash is not null && rt.RevokedAt > DateTimeOffset.UtcNow.AddSeconds(-30))
                    return Results.Unauthorized();
                // A revoked token was re-used later → possible theft: revoke every token of this user.
                if (rt.RevokedAt is not null)
                    await db.RefreshTokens.Where(t => t.UserId == rt.UserId && t.RevokedAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow));
                return Results.Unauthorized();
            }
            return await IssueAsync(rt.User, db, tokens, http, rotated: rt);
        });

        g.MapPost("/logout", async (AppDbContext db, HttpContext http) =>
        {
            if (http.Request.Cookies.TryGetValue(RefreshCookie, out var raw))
            {
                var hash = TokenService.Hash(raw);
                await db.RefreshTokens.Where(t => t.TokenHash == hash && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow));
            }
            http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/auth" });
            return Results.NoContent();
        });

        app.MapGet("/api/me", async (HttpContext http, AppDbContext db) =>
        {
            var id = http.User.UserId();
            var u = await db.Users.Include(x => x.Preference).FirstAsync(x => x.Id == id);
            return ToDto(u);
        }).RequireAuthorization();

        // On-screen "O'g'il bola / Qiz bola" buttons during the getting-to-know talk.
        app.MapPut("/api/me/profile", async (ProfileRequest req, HttpContext http, AppDbContext db) =>
        {
            if (req.Gender is not ("male" or "female")) return Results.BadRequest();
            var id = http.User.UserId();
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Gender, req.Gender));
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static async Task<IResult> IssueAsync(User user, AppDbContext db, TokenService tokens,
        HttpContext http, RefreshToken? rotated = null)
    {
        var (access, expires) = tokens.CreateAccessToken(user);
        var (raw, hash, rtExpires) = tokens.CreateRefreshToken();
        if (rotated is not null)
        {
            rotated.RevokedAt = DateTimeOffset.UtcNow;
            rotated.ReplacedByHash = hash;
        }
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = hash, ExpiresAt = rtExpires });
        await db.SaveChangesAsync();

        http.Response.Cookies.Append(RefreshCookie, raw, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            Expires = rtExpires,
        });
        return Results.Ok(new AuthResponse(access, expires, ToDto(user)));
    }

    private static UserDto ToDto(User u) => new(u.Id, u.Username, u.DisplayName,
        u.Preference?.Language ?? "uz-UZ", u.Preference?.Voice ?? "uz-UZ-SardorNeural", u.Gender, u.Age, u.City, u.OnboardingStep);
}
