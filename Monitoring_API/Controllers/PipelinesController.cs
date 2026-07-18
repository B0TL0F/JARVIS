using Microsoft.AspNetCore.Mvc;
using Monitoring_API.Middleware;
using Monitoring_API.Models;
using Monitoring_API.Services;

namespace Monitoring_API.Controllers;

// Azure DevOps pipeline dashboard endpoints (ported from Sentinel's
// getAzurePipelines / getAzureBuildErrors socket handlers). Mostly read-only,
// available to any authenticated user — except /trigger, which is admin-only
// (it's the one write operation Jarvis performs against real infrastructure).
[ApiController]
[Route("api/pipelines")]
public class PipelinesController : ControllerBase
{
    private readonly AzureDevOpsService _azure;
    private readonly PipelineAnalysisService _analysis;
    private readonly ActivityLogger _activity;
    private readonly IServiceScopeFactory _scopeFactory;

    public PipelinesController(AzureDevOpsService azure, PipelineAnalysisService analysis, ActivityLogger activity, IServiceScopeFactory scopeFactory)
    {
        _azure = azure;
        _analysis = analysis;
        _activity = activity;
        _scopeFactory = scopeFactory;
    }

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

    [HttpGet]
    public async Task<ActionResult<PipelineDashboardDto>> Get(CancellationToken ct)
    {
        return Ok(await _azure.GetPipelineDashboardAsync(ct));
    }

    [HttpGet("build/{buildId:int}/errors")]
    public async Task<ActionResult<IEnumerable<BuildErrorRecordDto>>> GetBuildErrors(int buildId, CancellationToken ct)
    {
        return Ok(await _azure.GetBuildErrorsAsync(buildId, ct));
    }

    // Heuristic CI/CD insights (flakiness, duration anomalies, DORA-style
    // deployment frequency/change-failure-rate/MTTR) — computed on demand
    // from deeper Azure DevOps history, independent of the main dashboard call.
    [HttpGet("insights")]
    public async Task<ActionResult<object>> GetInsights(CancellationToken ct)
    {
        var dashboard = await _azure.GetPipelineDashboardAsync(ct);
        if (!dashboard.Ok)
        {
            return Ok(new { ok = false, msg = dashboard.Msg, buildInsights = Array.Empty<object>(), releaseInsights = Array.Empty<object>() });
        }

        // A real Azure DevOps project can have 80-100+ pipeline definitions,
        // most abandoned/never-run ("No recent builds" on the dashboard) —
        // querying 30-day history for those is pure wasted API calls, so skip
        // anything with no recent activity, and bound concurrency on the rest
        // so we don't hammer Azure DevOps (or its rate limiter) with 100
        // simultaneous requests.
        using var throttle = new SemaphoreSlim(8);

        async Task<T> Bounded<T>(Func<Task<T>> work)
        {
            await throttle.WaitAsync(ct);
            try { return await work(); }
            finally { throttle.Release(); }
        }

        var activeBuildDefs = dashboard.BuildPipelines.Where(d => d.RecentBuilds.Count > 0).ToList();
        var activeReleaseDefs = dashboard.ReleasePipelines.Where(d => d.RecentReleases.Count > 0).ToList();

        // Each bounded task resolves its own DI scope (own MonitoringDbContext, own
        // AzureDevOpsService/IClaudeService instances) — AzureDevOpsService and
        // CompositeAiService both read settings via a scoped DbContext, which is not
        // thread-safe; sharing one instance across these concurrent tasks throws
        // "A second operation was started on this context instance".
        var buildInsightTasks = activeBuildDefs.Select(def => Bounded(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var scopedAzure = scope.ServiceProvider.GetRequiredService<AzureDevOpsService>();
            var scopedAnalysis = scope.ServiceProvider.GetRequiredService<PipelineAnalysisService>();
            var scopedClaude = scope.ServiceProvider.GetRequiredService<IClaudeService>();

            var history = await scopedAzure.GetBuildHistoryAsync(def.Id, days: 30, ct);
            var insight = scopedAnalysis.ComputeBuildInsight(def.Id, def.Name, history);

            // Only spend an AI call on pipelines actually showing a problem —
            // healthy pipelines keep the (empty) fallback and skip the AI call entirely.
            if (insight.IsFlaky || insight.IsDurationAnomalous || insight.ConsecutiveFailures > 0)
            {
                var latestBuildId = history.OrderByDescending(b => b.FinishTime).FirstOrDefault()?.Id;
                var errors = latestBuildId is int bid ? await scopedAzure.GetBuildErrorsAsync(bid, ct) : new List<BuildErrorRecordDto>();
                insight.Summary = await scopedAnalysis.SummarizeBuildAsync(insight, errors, scopedClaude, ct);
            }

            return insight;
        }));

        var releaseInsightTasks = activeReleaseDefs.Select(def => Bounded(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var scopedAzure = scope.ServiceProvider.GetRequiredService<AzureDevOpsService>();
            var scopedAnalysis = scope.ServiceProvider.GetRequiredService<PipelineAnalysisService>();

            var history = await scopedAzure.GetReleaseHistoryAsync(def.Id, days: 30, ct);
            return scopedAnalysis.ComputeReleaseInsight(def.Id, def.Name, history);
        }));

        var buildInsights = await Task.WhenAll(buildInsightTasks);
        var releaseInsights = await Task.WhenAll(releaseInsightTasks);

        return Ok(new { ok = true, buildInsights, releaseInsights });
    }

    // Bulk-trigger build pipelines — the one write operation Jarvis performs
    // against Azure DevOps. Admin-only. Release/deployment pipelines are
    // intentionally not exposed here — queuing a build (compile/test) is
    // low-risk; deploying to real environments is a materially bigger one.
    // Triggered sequentially (not in parallel like /insights) so each item's
    // success/failure is unambiguous and cleanly audit-logged.
    [HttpPost("trigger")]
    public async Task<ActionResult<IEnumerable<TriggeredBuildDto>>> TriggerBuilds([FromBody] TriggerBuildsRequest req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;
        if (req.DefinitionIds is null || req.DefinitionIds.Count == 0)
        {
            return BadRequest(new { error = "No pipelines selected." });
        }

        var dashboard = await _azure.GetPipelineDashboardAsync(ct);
        var namesById = dashboard.BuildPipelines.ToDictionary(d => d.Id, d => d.Name);

        var results = new List<TriggeredBuildDto>();
        foreach (var id in req.DefinitionIds.Distinct())
        {
            var result = await _azure.QueueBuildAsync(id, req.Branch, ct);
            result.Name = namesById.TryGetValue(id, out var name) ? name : $"#{id}";
            results.Add(result);

            var branchSuffix = string.IsNullOrWhiteSpace(req.Branch) ? "" : $" on branch '{req.Branch}'";
            var detail = result.Ok
                ? $"Queued build #{result.BuildId} for '{result.Name}'{branchSuffix}"
                : $"Failed to queue build for '{result.Name}'{branchSuffix}: {result.Error}";
            await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
                "pipeline.trigger", detail, BasicAuthMiddleware.ClientIp(HttpContext));
        }

        return Ok(results);
    }

    // Cancels a running/queued build. Admin-only, mirrors the trigger endpoint's audit-log
    // pattern. Azure DevOps processes cancellation asynchronously — a successful response here
    // means cancellation was requested, not that the build has necessarily stopped yet.
    [HttpPost("build/{buildId:int}/cancel")]
    public async Task<ActionResult<TriggeredBuildDto>> CancelBuild(int buildId, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var result = await _azure.CancelBuildAsync(buildId, ct);

        var detail = result.Ok
            ? $"Requested cancellation of build #{buildId} (status now '{result.Status}')"
            : $"Failed to cancel build #{buildId}: {result.Error}";
        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "pipeline.cancel", detail, BasicAuthMiddleware.ClientIp(HttpContext));

        return Ok(result);
    }

    // Deletes a build pipeline definition entirely. Admin-only, irreversible on the Azure
    // DevOps side — the chat confirm-first flow (and this endpoint's own admin gate) are the
    // only safeguards before this actually removes the pipeline.
    [HttpDelete("definitions/{definitionId:int}")]
    public async Task<ActionResult<PipelineActionResultDto>> DeleteDefinition(int definitionId, [FromQuery] string? name, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var result = await _azure.DeleteBuildDefinitionAsync(definitionId, name ?? $"#{definitionId}", ct);

        var detail = result.Ok
            ? $"Deleted pipeline definition '{result.Name}' (#{definitionId})"
            : $"Failed to delete pipeline definition #{definitionId}: {result.Error}";
        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "pipeline.delete", detail, BasicAuthMiddleware.ClientIp(HttpContext));

        return Ok(result);
    }

    // Renames a build pipeline definition. Admin-only.
    [HttpPut("definitions/{definitionId:int}/rename")]
    public async Task<ActionResult<PipelineActionResultDto>> RenameDefinition(int definitionId, [FromBody] RenamePipelineRequest req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;
        if (string.IsNullOrWhiteSpace(req.NewName)) return BadRequest(new { error = "New name is required." });

        var result = await _azure.RenameBuildDefinitionAsync(definitionId, req.OldName ?? $"#{definitionId}", req.NewName.Trim(), ct);

        var detail = result.Ok
            ? $"Renamed pipeline definition #{definitionId} to '{result.Name}'"
            : $"Failed to rename pipeline definition #{definitionId}: {result.Error}";
        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "pipeline.rename", detail, BasicAuthMiddleware.ClientIp(HttpContext));

        return Ok(result);
    }
}
