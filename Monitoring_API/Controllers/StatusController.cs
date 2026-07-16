using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring_API.Data;
using Monitoring_API.Models;
using Monitoring_API.Services;

namespace Monitoring_API.Controllers;

[ApiController]
[Route("api")]
public class StatusController : ControllerBase
{
    private readonly MonitoringDbContext _db;
    private readonly MonitoringTargetsOptions _options;
    private readonly ITargetProvider _targetProvider;

    public StatusController(MonitoringDbContext db, IOptions<MonitoringTargetsOptions> options, ITargetProvider targetProvider)
    {
        _db = db;
        _options = options.Value;
        _targetProvider = targetProvider;
    }

    [HttpGet("environments")]
    public async Task<ActionResult<object>> GetEnvironments(CancellationToken ct)
    {
        var environments = await _targetProvider.GetEnvironmentsAsync(ct);
        return Ok(new
        {
            environments = environments.Select(e => e.Name),
            pollingIntervalSeconds = _options.PollingIntervalSeconds
        });
    }

    [HttpGet("status")]
    public async Task<ActionResult<object>> Get([FromQuery] string? environment, CancellationToken ct)
    {
        var environments = await _targetProvider.GetEnvironmentsAsync(ct);
        var env = environments.FirstOrDefault(e =>
            string.Equals(e.Name, environment, StringComparison.OrdinalIgnoreCase))
            ?? environments.FirstOrDefault();

        if (env is null)
        {
            return NotFound(new { error = "No environments configured." });
        }

        var since24h = DateTime.UtcNow.AddHours(-24);

        // Latest result per (service, checkType) and 24h up/total counts, each in a single
        // grouped query — avoids the N+1 query-per-module pattern (this environment alone
        // can have 10+ modules, and there are now 14 environments total).
        var latestByKey = await _db.StatusChecks
            .Where(s => s.Environment == env.Name)
            .GroupBy(s => new { s.ServiceName, s.CheckType })
            .Select(g => g.OrderByDescending(x => x.TimestampUtc).First())
            .ToDictionaryAsync(s => (s.ServiceName, s.CheckType), ct);

        var uptimeByService = await _db.StatusChecks
            .Where(s => s.Environment == env.Name && s.TimestampUtc >= since24h)
            .GroupBy(s => s.ServiceName)
            .Select(g => new { Module = g.Key, Total = g.Count(), Up = g.Count(x => x.IsUp) })
            .ToDictionaryAsync(x => x.Module, ct);

        var modules = new List<ServiceStatusDto>();

        foreach (var target in env.Targets)
        {
            var hasApi = !string.IsNullOrWhiteSpace(target.ApiHost) && !string.IsNullOrWhiteSpace(target.RoutePrefix);

            latestByKey.TryGetValue((target.Module, CheckType.Api), out var latestApi);
            latestByKey.TryGetValue((target.Module, CheckType.Db), out var latestDb);

            var uptime = uptimeByService.TryGetValue(target.Module, out var stats) && stats.Total > 0
                ? Math.Round(100.0 * stats.Up / stats.Total, 1)
                : 0;

            modules.Add(new ServiceStatusDto
            {
                Module = target.Module,
                Api = hasApi ? ToDto(latestApi) : null,
                Db = hasApi ? ToDto(latestDb) : null,
                UptimePercent24h = uptime
            });
        }

        return Ok(new
        {
            environment = env.Name,
            pollingIntervalSeconds = _options.PollingIntervalSeconds,
            generatedAtUtc = DateTime.UtcNow,
            modules
        });
    }

    private static CheckResultDto ToDto(StatusCheck? check) => check is null
        ? new CheckResultDto { IsUp = false, ErrorMessage = "No data yet" }
        : new CheckResultDto
        {
            IsUp = check.IsUp,
            ResponseTimeMs = check.ResponseTimeMs,
            HttpStatusCode = check.HttpStatusCode,
            ErrorMessage = check.ErrorMessage,
            TimestampUtc = check.TimestampUtc
        };
}
