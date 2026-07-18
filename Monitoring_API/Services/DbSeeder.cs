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

        // EnsureCreatedAsync only creates the whole schema on a brand-new database — it
        // will NOT retrofit a newly-added table (AlertHistory) onto a database that was
        // already provisioned before this feature existed. This idempotent DDL step
        // covers that gap without introducing a full EF Core migrations project, which
        // isn't warranted for a single additive table (no existing-table column changes).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AlertHistories" (
                "Id" SERIAL PRIMARY KEY,
                "Environment" VARCHAR(50) NOT NULL,
                "Module" VARCHAR(100) NOT NULL,
                "AlertType" VARCHAR(30) NOT NULL,
                "DedupKey" VARCHAR(200) NOT NULL,
                "FirstFiredAtUtc" TIMESTAMP NOT NULL,
                "LastFiredAtUtc" TIMESTAMP NOT NULL,
                "FireCount" INTEGER NOT NULL,
                "ResolvedAtUtc" TIMESTAMP NULL,
                "Summary" VARCHAR(2000) NULL,
                "TeamsSent" BOOLEAN NOT NULL,
                "EmailSent" BOOLEAN NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_AlertHistories_DedupKey" ON "AlertHistories" ("DedupKey");
            CREATE INDEX IF NOT EXISTS "IX_AlertHistories_Environment_LastFiredAtUtc" ON "AlertHistories" ("Environment", "LastFiredAtUtc");

            CREATE TABLE IF NOT EXISTS "RemediationRules" (
                "Id" SERIAL PRIMARY KEY,
                "Environment" VARCHAR(50) NOT NULL,
                "Module" VARCHAR(100) NOT NULL,
                "AzureDevOpsDefinitionId" INTEGER NOT NULL,
                "Branch" VARCHAR(255) NULL,
                "Enabled" BOOLEAN NOT NULL,
                "MaxActionsPerHour" INTEGER NOT NULL,
                "UpdatedAtUtc" TIMESTAMP NOT NULL,
                "UpdatedBy" VARCHAR(100) NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_RemediationRules_Environment_Module" ON "RemediationRules" ("Environment", "Module");

            CREATE TABLE IF NOT EXISTS "RemediationHistories" (
                "Id" SERIAL PRIMARY KEY,
                "Environment" VARCHAR(50) NOT NULL,
                "Module" VARCHAR(100) NOT NULL,
                "DedupKey" VARCHAR(200) NOT NULL,
                "RuleId" INTEGER NOT NULL,
                "Action" VARCHAR(30) NOT NULL,
                "Ok" BOOLEAN NOT NULL,
                "BuildId" INTEGER NULL,
                "Error" VARCHAR(1000) NULL,
                "FiredAtUtc" TIMESTAMP NOT NULL,
                "ResolvedAtUtc" TIMESTAMP NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_RemediationHistories_DedupKey_FiredAtUtc" ON "RemediationHistories" ("DedupKey", "FiredAtUtc");
            """, ct);

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
