using System.Text;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

// Gathers live data for the "Ask Jarvis" chat assistant by calling into the SAME
// services/queries the Dashboard/History/Pipelines endpoints already use — never
// recomputes status/incident/pipeline logic itself. Renders it as compact structured
// text that gets injected into Claude's system prompt so answers are grounded in real
// current data, not general knowledge.
public class ChatContextService
{
    private readonly ITargetProvider _targetProvider;
    private readonly StatusSnapshotService _snapshot;
    private readonly MonitoringDbContext _db;
    private readonly IncidentAnalysisService _incidentAnalysis;
    private readonly AzureDevOpsService _azure;
    private readonly PipelineAnalysisService _pipelineAnalysis;

    public ChatContextService(
        ITargetProvider targetProvider,
        StatusSnapshotService snapshot,
        MonitoringDbContext db,
        IncidentAnalysisService incidentAnalysis,
        AzureDevOpsService azure,
        PipelineAnalysisService pipelineAnalysis)
    {
        _targetProvider = targetProvider;
        _snapshot = snapshot;
        _db = db;
        _incidentAnalysis = incidentAnalysis;
        _azure = azure;
        _pipelineAnalysis = pipelineAnalysis;
    }

    // minimal=true skips the incident/pipeline-failure narrative sections — used for the
    // admin tool-enabled path, which already sends pipeline/target/user catalogs plus up to
    // 11 tool schemas in the same request. Combined, the full context can exceed tighter
    // free-tier provider budgets (e.g. Groq's 6K TPM); actions rarely need incident history
    // anyway, just current status.
    public async Task<string> BuildContextAsync(string? environment, CancellationToken ct, bool minimal = false)
    {
        var sb = new StringBuilder();
        // Fixed +5:30 offset (IST has no DST) — avoids depending on the tzdata package, which
        // isn't installed in the aspnet runtime base image.
        var istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        sb.AppendLine($"Current date/time (IST): {istNow:yyyy-MM-dd HH:mm:ss} IST");
        sb.AppendLine();
        var environments = await _targetProvider.GetEnvironmentsAsync(ct);

        // Exact match first; if none, fall back to a PREFIX match (e.g. "PROD" matching
        // PROD_BUD/PROD_CES/PROD_ACS/...) so "what failed in PROD this week" resolves across
        // every environment sharing that prefix, not just an exact env name.
        var scoped = string.IsNullOrWhiteSpace(environment)
            ? environments
            : environments.Where(e => string.Equals(e.Name, environment, StringComparison.OrdinalIgnoreCase)).ToList();
        if (scoped.Count == 0 && !string.IsNullOrWhiteSpace(environment))
        {
            scoped = environments.Where(e => e.Name.StartsWith(environment, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (scoped.Count == 0)
        {
            sb.AppendLine("No matching environment found for the requested scope.");
            return sb.ToString();
        }

        // When a specific environment (or a small prefix-matched group, e.g. "PROD" -> 5 envs)
        // is requested, give full per-module detail. When scoped across all ~30 environments, a
        // full per-module dump is tens of thousands of tokens — too large for tighter free-tier
        // providers (e.g. Groq). In that case, only surface DOWN modules plus a healthy-count summary.
        const int MaxEnvironmentsForFullDetail = 5;
        var singleEnvironment = !string.IsNullOrWhiteSpace(environment) && scoped.Count <= MaxEnvironmentsForFullDetail;

        // Hard cap on individually-listed down modules across an unscoped (all-environments)
        // query — this sandbox alone can have hundreds of down modules (unreachable test
        // hostnames), and without a cap the prompt can reach tens of thousands of tokens,
        // exceeding free-tier provider limits (e.g. Groq's 6K TPM). A single-environment
        // question is never capped — it's already small.
        const int MaxDownModulesListed = 25;

        // Full per-module dump only for exactly one environment — a prefix match like "PROD"
        // (up to MaxEnvironmentsForFullDetail environments) still gets down-only + a count
        // summary here, same as the fully-unscoped path, just narrowed to the matched group.
        var showAllModules = scoped.Count == 1;

        sb.AppendLine("=== CURRENT MODULE STATUS ===");
        var downCount = 0;
        var totalCount = 0;
        var downListed = 0;
        var downEnvNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var env in scoped)
        {
            var modules = await _snapshot.GetModuleStatusesAsync(env, ct);
            foreach (var m in modules)
            {
                totalCount++;
                var apiUp = m.Api is null || m.Api.IsUp;
                var dbUp = m.Db is null || m.Db.IsUp;
                var isDown = !apiUp || !dbUp;
                if (isDown)
                {
                    downCount++;
                    downEnvNames.Add(env.Name);
                }

                var shouldList = showAllModules || (isDown && downListed < MaxDownModulesListed);
                if (shouldList)
                {
                    var apiState = m.Api is null ? "n/a" : (m.Api.IsUp ? "UP" : $"DOWN ({m.Api.ErrorMessage})");
                    var dbState = m.Db is null ? "n/a" : (m.Db.IsUp ? "UP" : $"DOWN ({m.Db.ErrorMessage})");
                    sb.AppendLine($"{env.Name} / {m.Module}: API={apiState}, DB={dbState}, uptime24h={m.UptimePercent24h}%");
                    if (!showAllModules && isDown) downListed++;
                }
            }
        }
        if (!showAllModules)
        {
            if (downCount > downListed)
            {
                sb.AppendLine($"(+{downCount - downListed} more down modules not shown — ask about a specific environment for the full list.)");
            }
            sb.AppendLine($"({totalCount - downCount} other modules across {scoped.Count} environments are UP and not listed individually.)");
        }

        if (minimal)
        {
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine("=== RECENT INCIDENTS (last 7 days) ===");
        // Only pull detailed incident history for a single explicitly-requested environment —
        // across all environments this can be enormous (hundreds of down modules in this
        // sandbox), so an unscoped question gets the module-status summary above only.
        if (!singleEnvironment)
        {
            sb.AppendLine("Incident detail is only pulled for a specific environment — ask \"what's happening in <environment>\" for full incident history there.");
        }
        else
        {
            // Per-environment cap shrinks as more environments are in scope (e.g. a "PROD"
            // prefix match pulling in 5 environments at once) — a flat Take(10) per environment
            // multiplies into an oversized prompt once scoped.Count > 1.
            var incidentsPerEnv = Math.Max(2, 10 / scoped.Count);
            var since = DateTime.UtcNow.AddDays(-7);
            foreach (var env in scoped)
            {
                var checks = await _db.StatusChecks
                    .Where(c => c.Environment == env.Name && c.TimestampUtc >= since
                        && (c.CheckType == CheckType.Api || c.CheckType == CheckType.Db))
                    .OrderBy(c => c.TimestampUtc)
                    .ToListAsync(ct);

                var incidents = checks
                    .GroupBy(c => (c.ServiceName, c.CheckType))
                    .SelectMany(g => _incidentAnalysis.ComputeIncidents(g.Key.ServiceName, g.Key.CheckType, g.ToList()))
                    .OrderByDescending(i => i.StartedAtUtc)
                    .Take(incidentsPerEnv)
                    .ToList();

                if (incidents.Count == 0)
                {
                    sb.AppendLine($"{env.Name}: no incidents in the last 7 days.");
                }
                foreach (var i in incidents)
                {
                    sb.AppendLine($"{env.Name} / {i.Module}: {i.Summary}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== RECENT ALERTS (last 7 days) ===");
        // Same single/small-environment-only scoping as incidents above — lets "why did this
        // alert fire" / "explain this alert" questions be answered from real AlertHistory rows.
        if (!singleEnvironment)
        {
            sb.AppendLine("Alert detail is only pulled for a specific environment — ask about one to see it.");
        }
        else
        {
            var since7d = DateTime.UtcNow.AddDays(-7);
            var envNames = scoped.Select(e => e.Name).ToList();
            var alerts = await _db.AlertHistories
                .Where(a => envNames.Contains(a.Environment) && a.LastFiredAtUtc >= since7d)
                .OrderByDescending(a => a.LastFiredAtUtc)
                .Take(20)
                .ToListAsync(ct);

            if (alerts.Count == 0)
            {
                sb.AppendLine("No alerts fired in the last 7 days for this scope.");
            }
            foreach (var a in alerts)
            {
                var state = a.ResolvedAtUtc is null ? "still open" : $"resolved at {a.ResolvedAtUtc:yyyy-MM-dd HH:mm} UTC";
                sb.AppendLine($"{a.Environment} / {a.Module} [{a.AlertType}]: {a.Summary} (fired {a.FireCount}x, first {a.FirstFiredAtUtc:yyyy-MM-dd HH:mm} UTC, {state})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== RECENT PIPELINE FAILURES ===");
        try
        {
            var dashboard = await _azure.GetPipelineDashboardAsync(ct);
            if (!dashboard.Ok)
            {
                sb.AppendLine("Azure DevOps is not configured.");
            }
            else
            {
                var failing = dashboard.BuildPipelines.Where(d => d.LatestStatus is "failed" or "partiallySucceeded").ToList();
                if (failing.Count == 0)
                {
                    sb.AppendLine("No build pipelines currently failing.");
                }
                foreach (var def in failing.Take(15))
                {
                    sb.AppendLine($"Pipeline '{def.Name}': latest status {def.LatestStatus}, build #{def.LatestBuildNumber}");
                }
            }
        }
        catch (Exception)
        {
            sb.AppendLine("Pipeline data unavailable right now.");
        }

        return sb.ToString();
    }

    // Used by ChatController to (a) tell Claude which exact pipeline names it may choose from
    // when the trigger_build tool is offered, and (b) validate the pipeline name Claude returns
    // against real Azure DevOps data before treating it as a legitimate pending action.
    public async Task<List<BuildPipelineDto>> GetBuildPipelineCatalogAsync(CancellationToken ct)
    {
        var dashboard = await _azure.GetPipelineDashboardAsync(ct);
        return dashboard.Ok ? dashboard.BuildPipelines : new List<BuildPipelineDto>();
    }

    // Used the same way for the target-management tools (create/update/delete a monitored
    // target) — lets Claude match "the PCS module in QA" to a real (Id, Environment, Module)
    // row rather than inventing one, and validates its answer against this list afterward.
    public async Task<List<ImportedTarget>> GetTargetsCatalogAsync(CancellationToken ct)
    {
        return await _db.ImportedTargets.AsNoTracking().OrderBy(t => t.Environment).ThenBy(t => t.Module).ToListAsync(ct);
    }

    // Every real environment name — used by ChatController to detect an environment (or a
    // shared prefix like "PROD") mentioned in free-text chat questions, so BuildContextAsync
    // can be scoped without requiring the dashboard's environment dropdown to be set.
    public async Task<List<string>> GetEnvironmentNamesAsync(CancellationToken ct)
    {
        var environments = await _targetProvider.GetEnvironmentsAsync(ct);
        return environments.Select(e => e.Name).ToList();
    }

    // Used by the create_remediation_rule tool to validate (environment, module) against every
    // actually-monitored module — not just manual ImportedTarget overrides, since most modules
    // come from config/targets.json instead.
    public async Task<List<(string Environment, string Module)>> GetMonitoredModulesCatalogAsync(CancellationToken ct)
    {
        var environments = await _targetProvider.GetEnvironmentsAsync(ct);
        return environments
            .SelectMany(e => e.Targets.Select(t => (Environment: e.Name, Module: t.Module)))
            .Distinct()
            .OrderBy(x => x.Environment).ThenBy(x => x.Module)
            .ToList();
    }

    // Read-only listing for "what remediation rules exist" questions — no tool/confirm needed
    // since nothing is mutated, just fed into the prompt like the pipeline/target/user catalogs.
    public async Task<List<RemediationRule>> GetRemediationRulesCatalogAsync(CancellationToken ct)
    {
        return await _db.RemediationRules.AsNoTracking()
            .OrderBy(r => r.Environment).ThenBy(r => r.Module)
            .ToListAsync(ct);
    }

    // Used the same way for the user-management tools (delete/change-role) — never for
    // create, since a new user's password is generated server-side, not supplied by the model.
    public async Task<List<AppUser>> GetUsersCatalogAsync(CancellationToken ct)
    {
        return await _db.AppUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
    }
}
