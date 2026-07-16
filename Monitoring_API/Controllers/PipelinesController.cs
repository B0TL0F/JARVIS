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

    public PipelinesController(AzureDevOpsService azure, PipelineAnalysisService analysis, ActivityLogger activity)
    {
        _azure = azure;
        _analysis = analysis;
        _activity = activity;
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

        var buildInsightTasks = activeBuildDefs.Select(def => Bounded(async () =>
        {
            var history = await _azure.GetBuildHistoryAsync(def.Id, days: 30, ct);
            return _analysis.ComputeBuildInsight(def.Id, def.Name, history);
        }));

        var releaseInsightTasks = activeReleaseDefs.Select(def => Bounded(async () =>
        {
            var history = await _azure.GetReleaseHistoryAsync(def.Id, days: 30, ct);
            return _analysis.ComputeReleaseInsight(def.Id, def.Name, history);
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
}
