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
        var environments = await _targetProvider.GetEnvironmentsAsync(ct);

        var scoped = string.IsNullOrWhiteSpace(environment)
            ? environments
            : environments.Where(e => string.Equals(e.Name, environment, StringComparison.OrdinalIgnoreCase)).ToList();

        if (scoped.Count == 0)
        {
            sb.AppendLine("No matching environment found for the requested scope.");
            return sb.ToString();
        }

        // When a specific environment is requested, give full per-module detail (small, bounded
        // to one environment). When scoped across all ~30 environments, a full per-module dump
        // is tens of thousands of tokens — too large for tighter free-tier providers (e.g. Groq).
        // In that case, only surface DOWN modules plus a healthy-count summary.
        var singleEnvironment = !string.IsNullOrWhiteSpace(environment);

        // Hard cap on individually-listed down modules across an unscoped (all-environments)
        // query — this sandbox alone can have hundreds of down modules (unreachable test
        // hostnames), and without a cap the prompt can reach tens of thousands of tokens,
        // exceeding free-tier provider limits (e.g. Groq's 6K TPM). A single-environment
        // question is never capped — it's already small.
        const int MaxDownModulesListed = 25;

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

                var shouldList = singleEnvironment || (isDown && downListed < MaxDownModulesListed);
                if (shouldList)
                {
                    var apiState = m.Api is null ? "n/a" : (m.Api.IsUp ? "UP" : $"DOWN ({m.Api.ErrorMessage})");
                    var dbState = m.Db is null ? "n/a" : (m.Db.IsUp ? "UP" : $"DOWN ({m.Db.ErrorMessage})");
                    sb.AppendLine($"{env.Name} / {m.Module}: API={apiState}, DB={dbState}, uptime24h={m.UptimePercent24h}%");
                    if (!singleEnvironment && isDown) downListed++;
                }
            }
        }
        if (!singleEnvironment)
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
                    .Take(10)
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

    // Used the same way for the user-management tools (delete/change-role) — never for
    // create, since a new user's password is generated server-side, not supplied by the model.
    public async Task<List<AppUser>> GetUsersCatalogAsync(CancellationToken ct)
    {
        return await _db.AppUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
    }
}
