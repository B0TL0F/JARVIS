using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

// Ocelot API-gateway auto-importer (ported from Sentinel's ocelot-importer.js +
// ocelot-seed.py). Parses an Ocelot gateway config, extracts the "verifyapi/check"
// health routes, and turns each into an ImportedTarget grouped under an environment
// name derived from the filename. Uses System.Text.Json with comment/trailing-comma
// tolerance, so no Python runtime is needed (Sentinel shelled out to Python only to
// strip JSONC comments).
public class OcelotImportService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OcelotImportService> _logger;

    // filename tag -> environment/group name. The first 5 are Sentinel's original
    // mapping; the rest were added after auditing every ocelot/*.json file against
    // config/targets.json by hostname — without these, the importer fell back to
    // the raw filename tag and created a second, wrongly-named environment for the
    // same real environment (e.g. "CESUAT" instead of merging into "UAT_CES"),
    // which is why modules like Login/Logs/Report appeared to be "missing" from
    // UAT_CES/UAT_ACS/UAT_ACS_IGA — they were actually sitting under the duplicate.
    private static readonly Dictionary<string, string> TagToEnv = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TESTINGD"] = "DEV",
        ["TESTINGS"] = "STAGING",
        ["TESTINGU"] = "UAT",
        ["TESTINGP"] = "PROD",
        ["TESTINGQ"] = "QA",

        ["CESUAT"] = "UAT_CES",
        ["ACSUAT"] = "UAT_ACS",
        ["IGAUAT"] = "UAT_ACS_IGA",
        ["CESPROD"] = "PROD_CES",
        ["ACSPROD"] = "PROD_ACS",
        ["MPAUAT"] = "UAT_MPA",
        ["ACSPRODBUD"] = "PROD_BUD",
        ["ACSBOG"] = "PROD_BOG",
        ["POSTGRESQL"] = "POST_GRE",
        ["PRODLPMS"] = "PROD_LPMS",
    };

    private readonly string _ocelotDir;

    public OcelotImportService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<OcelotImportService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _ocelotDir = config["Ocelot:Directory"] is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "ocelot");
    }

    public string OcelotDirectory => _ocelotDir;

    // Re-scan the ocelot directory and import every file. When force=true, all
    // previously-imported targets are cleared first so the DB fully reflects the
    // current files (use for the "Re-import" settings button). When force=false,
    // environments already imported are left untouched (startup behaviour).
    public async Task<object> ReimportAllAsync(bool force, CancellationToken ct)
    {
        if (!Directory.Exists(_ocelotDir))
        {
            return new { imported = false, msg = $"Ocelot directory not found: {_ocelotDir}", environments = Array.Empty<object>() };
        }

        if (force)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            await db.ImportedTargets.ExecuteDeleteAsync(ct);
            _logger.LogInformation("[OCELOT] Force re-import — cleared existing imported targets");
        }

        var files = Directory.EnumerateFiles(_ocelotDir)
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();

        foreach (var file in files)
        {
            await ImportFileAsync(file, ct);
        }

        using var summaryScope = _scopeFactory.CreateScope();
        var summaryDb = summaryScope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var envs = await summaryDb.ImportedTargets
            .GroupBy(t => t.Environment)
            .Select(g => new { environment = g.Key, count = g.Count() })
            .OrderBy(x => x.environment)
            .ToListAsync(ct);

        return new { imported = true, filesScanned = files.Count, environments = envs };
    }

    public async Task ImportFileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("[OCELOT] File not found: {File}", filePath);
                return;
            }

            var content = await File.ReadAllTextAsync(filePath, ct);
            var groupName = GroupNameFromFile(filePath);
            var fileName = Path.GetFileName(filePath);

            var targets = ParseTargets(content, groupName);
            if (targets.Count == 0)
            {
                _logger.LogInformation("[OCELOT] No verifyapi/check routes in {File}", fileName);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();

            // Skip if this environment was already imported — preserves history and
            // matches Sentinel's "group already exists" behaviour.
            if (await db.ImportedTargets.AnyAsync(t => t.Environment == groupName, ct))
            {
                _logger.LogInformation("[OCELOT] Environment '{Env}' already imported — skipping", groupName);
                return;
            }

            foreach (var t in targets)
            {
                t.SourceFile = fileName;
                t.Origin = "ocelot";
                t.CreatedAtUtc = DateTime.UtcNow;
                db.ImportedTargets.Add(t);
            }
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("[OCELOT] Imported {Count} targets into environment '{Env}' from {File}",
                targets.Count, groupName, fileName);
        }
        catch (DbUpdateException ex)
        {
            // Unique (Environment, Module) index — a concurrent import raced us; safe to ignore.
            _logger.LogWarning(ex, "[OCELOT] Duplicate import ignored for {File}", Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OCELOT] Import failed for {File}", Path.GetFileName(filePath));
        }
    }

    // Parse the Ocelot JSON and extract one ImportedTarget per unique verifyapi/check route.
    private List<ImportedTarget> ParseTargets(string content, string groupName)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        using var doc = JsonDocument.Parse(content, options);
        var root = doc.RootElement;

        JsonElement routes = default;
        if (!TryGetArray(root, "Routes", out routes) && !TryGetArray(root, "ReRoutes", out routes))
        {
            return new();
        }

        var seenModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ImportedTarget>();

        foreach (var route in routes.EnumerateArray())
        {
            var downstream = GetString(route, "DownstreamPathTemplate");
            if (string.IsNullOrEmpty(downstream) || !downstream.Contains("verifyapi/check", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!route.TryGetProperty("DownstreamHostAndPorts", out var hosts)
                || hosts.ValueKind != JsonValueKind.Array || hosts.GetArrayLength() == 0)
                continue;

            var host = GetString(hosts[0], "Host");
            if (string.IsNullOrEmpty(host)) continue;

            // Route prefix is the leading "api_*" path segment.
            var segments = downstream.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var routePrefix = Array.Find(segments, s => s.StartsWith("api_", StringComparison.OrdinalIgnoreCase))
                              ?? host.Split('.')[0];

            var module = routePrefix.Replace("api_", "", StringComparison.OrdinalIgnoreCase)
                                    .Replace("_", " ")
                                    .ToUpperInvariant();
            // De-dupe by module (route prefix). Many gateways route every module through
            // one shared downstream host, so de-duping by host would wrongly collapse them
            // all to a single target — the module/route prefix is the unique key.
            if (!seenModules.Add(module)) continue;

            result.Add(new ImportedTarget
            {
                Environment = groupName,
                Module = module,
                ApiHost = host,
                RoutePrefix = routePrefix
            });
        }

        return result;
    }

    // "Ocelot.TestingD.JSON" -> tag "TESTINGD" -> "DEV" (falls back to the tag itself).
    private static string GroupNameFromFile(string filePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(filePath); // e.g. Ocelot.TestingD
        var parts = baseName.Split('.');
        var tag = (parts.Length > 1 ? parts[1] : parts[0]).ToUpperInvariant();
        return TagToEnv.TryGetValue(tag, out var env) ? env : tag;
    }

    private static bool TryGetArray(JsonElement el, string prop, out JsonElement arr)
    {
        if (el.TryGetProperty(prop, out arr) && arr.ValueKind == JsonValueKind.Array)
            return true;
        arr = default;
        return false;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
