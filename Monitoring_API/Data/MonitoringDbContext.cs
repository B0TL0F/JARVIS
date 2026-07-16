using Microsoft.EntityFrameworkCore;
using Monitoring_API.Models;

namespace Monitoring_API.Data;

public class MonitoringDbContext : DbContext
{
    public MonitoringDbContext(DbContextOptions<MonitoringDbContext> options) : base(options)
    {
    }

    public DbSet<StatusCheck> StatusChecks => Set<StatusCheck>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<ImportedTarget> ImportedTargets => Set<ImportedTarget>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StatusCheck>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Environment).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ServiceName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
            entity.HasIndex(e => new { e.Environment, e.ServiceName, e.CheckType, e.TimestampUtc });
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.Role).HasMaxLength(20).IsRequired();
            entity.HasIndex(e => e.Username).IsUnique();
        });

        modelBuilder.Entity<ActivityLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Details).HasMaxLength(2000);
            entity.Property(e => e.Ip).HasMaxLength(64);
            entity.HasIndex(e => e.CreatedAtUtc);
        });

        modelBuilder.Entity<ImportedTarget>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Environment).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Module).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ApiHost).HasMaxLength(255).IsRequired();
            entity.Property(e => e.RoutePrefix).HasMaxLength(255).IsRequired();
            entity.Property(e => e.Origin).HasMaxLength(20).IsRequired();
            entity.Property(e => e.SourceFile).HasMaxLength(255);
            // One row per module per environment — the importer upserts on this key.
            entity.HasIndex(e => new { e.Environment, e.Module }).IsUnique();
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(100);
        });
    }
}
