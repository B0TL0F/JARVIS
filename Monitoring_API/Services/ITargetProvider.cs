using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring_API.Data;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

// Single source of truth for "what do we monitor": merges the static
// config/targets.json environments with any targets discovered by the Ocelot
// importer (stored in ImportedTargets). Both the poller and the status API read
// through this so imported targets are polled and shown just like configured ones.
public interface ITargetProvider
{
    Task<List<MonitoringEnvironment>> GetEnvironmentsAsync(CancellationToken ct);
}

public class TargetProvider : ITargetProvider
{
    private readonly MonitoringTargetsOptions _options;
    private readonly MonitoringDbContext _db;

    public TargetProvider(IOptions<MonitoringTargetsOptions> options, MonitoringDbContext db)
    {
        _options = options.Value;
        _db = db;
    }

    public async Task<List<MonitoringEnvironment>> GetEnvironmentsAsync(CancellationToken ct)
    {
        // Start from the static config, cloned so we never mutate the bound options.
        var merged = _options.Environments
            .Select(e => new MonitoringEnvironment
            {
                Name = e.Name,
                Targets = e.Targets.Select(t => new MonitoringTarget
                {
                    Module = t.Module,
                    RoutePrefix = t.RoutePrefix,
                    ApiHost = t.ApiHost
                }).ToList()
            })
            .ToList();

        var imported = await _db.ImportedTargets.AsNoTracking().ToListAsync(ct);
        if (imported.Count == 0) return merged;

        var byName = merged.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in imported.GroupBy(i => i.Environment))
        {
            if (!byName.TryGetValue(group.Key, out var env))
            {
                env = new MonitoringEnvironment { Name = group.Key };
                byName[group.Key] = env;
                merged.Add(env);
            }

            var byModule = env.Targets.ToDictionary(t => t.Module, StringComparer.OrdinalIgnoreCase);
            foreach (var it in group)
            {
                if (byModule.TryGetValue(it.Module, out var existing))
                {
                    // A DB row for a module that's also in the static config is an
                    // admin-made override — it wins over the config's values.
                    existing.ApiHost = it.ApiHost;
                    existing.RoutePrefix = it.RoutePrefix;
                }
                else
                {
                    env.Targets.Add(new MonitoringTarget
                    {
                        Module = it.Module,
                        ApiHost = it.ApiHost,
                        RoutePrefix = it.RoutePrefix
                    });
                }
            }
        }

        return merged;
    }
}
