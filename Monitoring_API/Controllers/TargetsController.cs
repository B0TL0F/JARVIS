using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring_API.Data;
using Monitoring_API.Middleware;
using Monitoring_API.Models;
using Monitoring_API.Services;

namespace Monitoring_API.Controllers;

// Full CRUD on every monitored target — both DB-managed ones (Ocelot-imported
// or manually-created) AND the static config/targets.json seeds, which are
// shown read-through here and become editable via the "override" endpoint
// (see Override()). This is the same merge TargetProvider does for polling,
// so what you see and edit here is exactly what's being monitored.
// All mutations are admin-only and audited.
[ApiController]
[Route("api/targets")]
public class TargetsController : ControllerBase
{
    private readonly MonitoringDbContext _db;
    private readonly ActivityLogger _activity;
    private readonly MonitoringTargetsOptions _options;

    public TargetsController(MonitoringDbContext db, ActivityLogger activity, IOptions<MonitoringTargetsOptions> options)
    {
        _db = db;
        _activity = activity;
        _options = options.Value;
    }

    private record ConfigKey(string Environment, string Module);

    private Dictionary<ConfigKey, (string ApiHost, string RoutePrefix)> ConfigIndex() =>
        _options.Environments
            .SelectMany(e => e.Targets.Select(t => (Env: e.Name, Target: t)))
            .ToDictionary(
                x => new ConfigKey(x.Env, x.Target.Module),
                x => (x.Target.ApiHost ?? string.Empty, x.Target.RoutePrefix ?? string.Empty),
                new ConfigKeyComparer());

    private class ConfigKeyComparer : IEqualityComparer<ConfigKey>
    {
        public bool Equals(ConfigKey? a, ConfigKey? b) =>
            a is not null && b is not null
            && string.Equals(a.Environment, b.Environment, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Module, b.Module, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(ConfigKey k) =>
            HashCode.Combine(k.Environment.ToLowerInvariant(), k.Module.ToLowerInvariant());
    }

    private static string? HealthCheckUrl(string? apiHost, string? routePrefix) =>
        string.IsNullOrWhiteSpace(apiHost) || string.IsNullOrWhiteSpace(routePrefix)
            ? null
            : $"https://{apiHost}/{routePrefix}/verifyapi/check";

    private bool RequireAdmin(out ActionResult? forbid)
    {
        if (!CurrentUser.IsAdmin(User))
        {
            forbid = StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin role required." });
            return false;
        }
        forbid = null;
        return true;
    }

    // Any authenticated user can view the full merged target list — static
    // config/targets.json seeds included, read-through, so the list here
    // matches exactly what's being monitored.
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TargetDto>>> Get(CancellationToken ct)
    {
        var configIndex = ConfigIndex();
        var dbTargets = await _db.ImportedTargets.AsNoTracking().ToListAsync(ct);
        var dbKeys = new HashSet<ConfigKey>(dbTargets.Select(t => new ConfigKey(t.Environment, t.Module)), new ConfigKeyComparer());

        var result = new List<TargetDto>();

        foreach (var t in dbTargets)
        {
            var key = new ConfigKey(t.Environment, t.Module);
            var origin = configIndex.ContainsKey(key) ? "override" : t.Origin;
            result.Add(new TargetDto
            {
                Id = t.Id,
                Environment = t.Environment,
                Module = t.Module,
                ApiHost = t.ApiHost,
                RoutePrefix = t.RoutePrefix,
                Origin = origin,
                HealthCheckUrl = HealthCheckUrl(t.ApiHost, t.RoutePrefix),
                CreatedAtUtc = t.CreatedAtUtc
            });
        }

        foreach (var (key, val) in configIndex)
        {
            if (dbKeys.Contains(key)) continue; // already represented above as an override
            result.Add(new TargetDto
            {
                Id = null,
                Environment = key.Environment,
                Module = key.Module,
                ApiHost = val.ApiHost,
                RoutePrefix = val.RoutePrefix,
                Origin = "config",
                HealthCheckUrl = HealthCheckUrl(val.ApiHost, val.RoutePrefix),
                CreatedAtUtc = null
            });
        }

        return Ok(result.OrderBy(t => t.Environment).ThenBy(t => t.Module));
    }

    [HttpPost]
    public async Task<ActionResult<TargetDto>> Create([FromBody] TargetUpsertRequest req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var error = Validate(req);
        if (error is not null) return BadRequest(new { error });

        if (await _db.ImportedTargets.AnyAsync(t => t.Environment == req.Environment && t.Module == req.Module, ct))
        {
            return Conflict(new { error = $"A target for '{req.Module}' already exists in '{req.Environment}'." });
        }

        if (ConfigIndex().ContainsKey(new ConfigKey(req.Environment, req.Module)))
        {
            return Conflict(new { error = $"'{req.Module}' in '{req.Environment}' is already defined in config/targets.json — edit it from the list instead of creating a new one." });
        }

        var target = new ImportedTarget
        {
            Environment = req.Environment.Trim(),
            Module = req.Module.Trim(),
            ApiHost = req.ApiHost?.Trim() ?? string.Empty,
            RoutePrefix = req.RoutePrefix?.Trim() ?? string.Empty,
            Origin = "manual",
            SourceFile = string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.ImportedTargets.Add(target);
        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "target.create", $"Created target '{target.Module}' in '{target.Environment}'", BasicAuthMiddleware.ClientIp(HttpContext));

        return CreatedAtAction(nameof(Get), ToDto(target));
    }

    // Edits a target that today is only defined in config/targets.json — creates
    // a DB row that takes precedence over the static config for this
    // (Environment, Module) from now on (see TargetProvider). Deleting that row
    // later (via DELETE /api/targets/{id}) reverts to the config default.
    [HttpPost("override")]
    public async Task<ActionResult<TargetDto>> Override([FromBody] TargetUpsertRequest req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var error = Validate(req);
        if (error is not null) return BadRequest(new { error });

        if (!ConfigIndex().ContainsKey(new ConfigKey(req.Environment, req.Module)))
        {
            return BadRequest(new { error = $"'{req.Module}' in '{req.Environment}' isn't defined in config/targets.json — use Create instead." });
        }

        if (await _db.ImportedTargets.AnyAsync(t => t.Environment == req.Environment && t.Module == req.Module, ct))
        {
            return Conflict(new { error = $"'{req.Module}' in '{req.Environment}' already has an override — edit it directly instead." });
        }

        var target = new ImportedTarget
        {
            Environment = req.Environment.Trim(),
            Module = req.Module.Trim(),
            ApiHost = req.ApiHost?.Trim() ?? string.Empty,
            RoutePrefix = req.RoutePrefix?.Trim() ?? string.Empty,
            Origin = "override",
            SourceFile = string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.ImportedTargets.Add(target);
        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "target.override", $"Overrode config-defined target '{target.Module}' in '{target.Environment}'", BasicAuthMiddleware.ClientIp(HttpContext));

        return CreatedAtAction(nameof(Get), ToDto(target));
    }

    // Bulk-sync many targets at once from an external source of truth (e.g. a
    // corrected export from another monitoring tool). For each item: if a DB
    // row already exists for (Environment, Module), its ApiHost/RoutePrefix
    // are updated in place (origin untouched); otherwise a new row is created
    // — "override" origin if the module is also in config/targets.json, else
    // "manual". One activity log entry summarizes the whole batch rather than
    // one per target.
    public class BulkUpsertResult
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    [HttpPost("bulk-upsert")]
    public async Task<ActionResult<BulkUpsertResult>> BulkUpsert([FromBody] List<TargetUpsertRequest> items, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var result = new BulkUpsertResult();
        var configIndex = ConfigIndex();
        var existing = await _db.ImportedTargets.ToListAsync(ct);
        var existingIndex = existing.ToDictionary(
            t => new ConfigKey(t.Environment, t.Module), t => t, new ConfigKeyComparer());

        foreach (var item in items)
        {
            var error = Validate(item);
            if (error is not null)
            {
                result.Skipped++;
                result.Errors.Add($"{item.Environment}/{item.Module}: {error}");
                continue;
            }

            var key = new ConfigKey(item.Environment, item.Module);
            var apiHost = item.ApiHost!.Trim();
            var routePrefix = item.RoutePrefix!.Trim();

            if (existingIndex.TryGetValue(key, out var row))
            {
                row.ApiHost = apiHost;
                row.RoutePrefix = routePrefix;
                result.Updated++;
            }
            else
            {
                var origin = configIndex.ContainsKey(key) ? "override" : "manual";
                var target = new ImportedTarget
                {
                    Environment = item.Environment.Trim(),
                    Module = item.Module.Trim(),
                    ApiHost = apiHost,
                    RoutePrefix = routePrefix,
                    Origin = origin,
                    SourceFile = string.Empty,
                    CreatedAtUtc = DateTime.UtcNow
                };
                _db.ImportedTargets.Add(target);
                existingIndex[key] = target; // guard against duplicate items in the same batch
                result.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "target.bulkUpsert",
            $"Bulk-synced targets: {result.Created} created, {result.Updated} updated, {result.Skipped} skipped",
            BasicAuthMiddleware.ClientIp(HttpContext));

        return Ok(result);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] TargetUpsertRequest req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var target = await _db.ImportedTargets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (target is null) return NotFound();

        var error = Validate(req);
        if (error is not null) return BadRequest(new { error });

        // Uniqueness on (Environment, Module) excluding self.
        if (await _db.ImportedTargets.AnyAsync(t => t.Id != id && t.Environment == req.Environment && t.Module == req.Module, ct))
        {
            return Conflict(new { error = $"A target for '{req.Module}' already exists in '{req.Environment}'." });
        }

        target.Environment = req.Environment.Trim();
        target.Module = req.Module.Trim();
        target.ApiHost = req.ApiHost?.Trim() ?? string.Empty;
        target.RoutePrefix = req.RoutePrefix?.Trim() ?? string.Empty;

        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "target.update", $"Updated target '{target.Module}' in '{target.Environment}'", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var target = await _db.ImportedTargets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (target is null) return NotFound();

        _db.ImportedTargets.Remove(target);
        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "target.delete", $"Deleted target '{target.Module}' in '{target.Environment}'", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    // Removes every target in an environment in one atomic operation — the
    // Targets UI's "Delete environment" action on a group header.
    [HttpDelete("environment/{environment}")]
    public async Task<IActionResult> DeleteEnvironment(string environment, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var targets = await _db.ImportedTargets.Where(t => t.Environment == environment).ToListAsync(ct);
        if (targets.Count == 0) return NotFound();

        _db.ImportedTargets.RemoveRange(targets);
        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "target.deleteEnvironment", $"Deleted environment '{environment}' ({targets.Count} target(s))", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    // A target must have something to probe: an API (host + route prefix). UI checks
    // were removed — a plain HTTP GET on a UI URL only proves the web server is alive,
    // not that the app works, so it gave false confidence and isn't monitored anymore.
    private static string? Validate(TargetUpsertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Environment)) return "Environment is required.";
        if (string.IsNullOrWhiteSpace(req.Module)) return "Module is required.";

        var hasApi = !string.IsNullOrWhiteSpace(req.ApiHost) && !string.IsNullOrWhiteSpace(req.RoutePrefix);
        if (!hasApi)
        {
            return "Provide an API host and route prefix to monitor.";
        }
        return null;
    }

    private static TargetDto ToDto(ImportedTarget t) => new()
    {
        Id = t.Id,
        Environment = t.Environment,
        Module = t.Module,
        ApiHost = t.ApiHost,
        RoutePrefix = t.RoutePrefix,
        Origin = t.Origin,
        HealthCheckUrl = HealthCheckUrl(t.ApiHost, t.RoutePrefix),
        CreatedAtUtc = t.CreatedAtUtc
    };
}
