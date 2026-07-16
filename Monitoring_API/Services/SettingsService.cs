using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

// Runtime settings backed by the AppSettings table. A DB value wins over the
// corresponding environment/appsettings value, so the settings page can override
// config without a redeploy. Config keys mirror the DB keys (e.g. "AzureDevOps:Pat").
public class SettingsService
{
    private readonly MonitoringDbContext _db;
    private readonly IConfiguration _config;

    public SettingsService(MonitoringDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    // DB value if present (and non-empty), otherwise the config/env fallback.
    public async Task<string?> GetEffectiveAsync(string key, CancellationToken ct = default)
    {
        var row = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is not null && !string.IsNullOrWhiteSpace(row.Value))
        {
            return row.Value;
        }
        return _config[key];
    }

    public async Task<bool> HasValueAsync(string key, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(await GetEffectiveAsync(key, ct));

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new AppSetting { Key = key };
            _db.AppSettings.Add(row);
        }
        row.Value = value;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
