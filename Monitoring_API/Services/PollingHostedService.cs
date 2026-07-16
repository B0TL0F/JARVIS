using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring_API.Data;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

public class PollingHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitoringTargetsOptions _options;
    private readonly ILogger<PollingHostedService> _logger;

    public PollingHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<MonitoringTargetsOptions> options,
        ILogger<PollingHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            // No EF migrations project (no local .NET SDK to author/run `dotnet ef` with) —
            // this is a single always-append table with no evolving schema, so
            // EnsureCreated is sufficient and keeps the container self-contained.
            await db.Database.EnsureCreatedAsync(stoppingToken);
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.PollingIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Polling cycle failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // shutting down
            }
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<HealthCheckerService>();
        var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var targetProvider = scope.ServiceProvider.GetRequiredService<ITargetProvider>();

        // Config targets merged with any Ocelot-imported targets.
        var environments = await targetProvider.GetEnvironmentsAsync(ct);

        var checkTasks = new List<Task<StatusCheck>>();

        foreach (var env in environments)
        {
            foreach (var target in env.Targets)
            {
                if (!string.IsNullOrWhiteSpace(target.ApiHost) && !string.IsNullOrWhiteSpace(target.RoutePrefix))
                {
                    var apiUrl = $"https://{target.ApiHost}/{target.RoutePrefix}/verifyapi/check";
                    var dbUrl = $"https://{target.ApiHost}/{target.RoutePrefix}/verifyapi/checksql";
                    checkTasks.Add(checker.CheckAsync(env.Name, target.Module, CheckType.Api, apiUrl, ct));
                    checkTasks.Add(checker.CheckAsync(env.Name, target.Module, CheckType.Db, dbUrl, ct));
                }

                // UI checks removed: a plain HTTP GET on a UI URL only proves the web
                // server is alive and routing, not that the app itself works — this gave
                // false confidence ("UP" on a broken page) without a real client-side
                // health signal, so it's been dropped rather than kept as a misleading check.
            }
        }

        var results = await Task.WhenAll(checkTasks);

        db.StatusChecks.AddRange(results);
        await db.SaveChangesAsync(ct);

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays));
        await db.StatusChecks.Where(s => s.TimestampUtc < cutoff).ExecuteDeleteAsync(ct);

        _logger.LogInformation("Polling cycle complete: {Count} checks across {EnvCount} environments, {UpCount} up",
            results.Length, environments.Count, results.Count(r => r.IsUp));
    }
}
