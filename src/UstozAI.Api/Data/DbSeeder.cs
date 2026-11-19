using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace UstozAI.Api.Data;

public class SeedOptions
{
    public string Username { get; set; } = "doston";
    public string DisplayName { get; set; } = "Doston";
    /// <summary>Supplied via config/env (Seed__Password). Never hard-coded in Production.</summary>
    public string? Password { get; set; }
}

public static class DbSeeder
{
    /// <summary>Applies pending migrations, then creates the default user if it doesn't exist.
    /// Idempotent: an existing user's password is never overwritten.</summary>
    public static async Task MigrateAndSeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");
        var opts = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SeedOptions>>().Value;
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        await db.Database.MigrateAsync();

        var username = opts.Username.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Username == username)) return;

        if (string.IsNullOrWhiteSpace(opts.Password))
        {
            log.LogWarning("Seed:Password is not set — default user '{User}' was NOT created.", username);
            return;
        }

        var user = new User { Username = username, DisplayName = opts.DisplayName };
        user.PasswordHash = hasher.HashPassword(user, opts.Password);
        user.Preference = new UserPreference();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        log.LogInformation("Seeded default user '{User}'.", username);
    }
}
