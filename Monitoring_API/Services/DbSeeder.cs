using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

// Zero-setup boot (ported from Sentinel's seeded-admin behaviour). Ensures the
// schema exists and creates a default admin from the BasicAuth config credential
// if no users exist yet — so the stack is usable out of the box with no wizard.
public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbSeeder");

        await db.Database.EnsureCreatedAsync(ct);

        if (await db.AppUsers.AnyAsync(ct))
        {
            return;
        }

        var username = config["BasicAuth:Username"];
        var password = config["BasicAuth:Password"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("No BasicAuth credential configured — skipping admin seed. " +
                              "No users exist, so login will be impossible until one is created.");
            return;
        }

        var admin = new AppUser
        {
            Username = username,
            Role = Roles.Admin,
            CreatedAtUtc = DateTime.UtcNow
        };
        admin.PasswordHash = hasher.HashPassword(admin, password);

        db.AppUsers.Add(admin);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Seeded default admin user '{Username}'", username);
    }
}
