using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

// Extracted from StatusController so the same "current module status" query is reused
// by both the /api/status endpoint and the chat assistant's context builder, instead of
// being duplicated.
public class StatusSnapshotService
{
    private readonly MonitoringDbContext _db;

    public StatusSnapshotService(MonitoringDbContext db)
    {
        _db = db;
    }

    public async Task<List<ServiceStatusDto>> GetModuleStatusesAsync(MonitoringEnvironment env, CancellationToken ct)
    {
        var since24h = DateTime.UtcNow.AddHours(-24);

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

        return modules;
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
