using Monitoring_API.Data;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

// Ported from Sentinel's activity-logger.js. Writes one audit row per action.
// Failures are swallowed (logged only) — auditing must never break the request.
public class ActivityLogger
{
    private readonly MonitoringDbContext _db;
    private readonly ILogger<ActivityLogger> _logger;

    public ActivityLogger(MonitoringDbContext db, ILogger<ActivityLogger> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(int? userId, string? username, string action, string? details, string? ip)
    {
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                UserId = userId,
                Username = string.IsNullOrWhiteSpace(username) ? "system" : username,
                Action = action,
                Details = details ?? string.Empty,
                Ip = ip,
                CreatedAtUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write activity log ({Action})", action);
        }
    }
}
