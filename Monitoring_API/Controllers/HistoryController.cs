using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Models;
using Monitoring_API.Services;

namespace Monitoring_API.Controllers;

// Exposes the incident/history log and heuristic "AI" insights derived from
// StatusChecks. Read-only, any authenticated user — same visibility tier as
// Dashboard/Pipelines.
[ApiController]
[Route("api/history")]
public class HistoryController : ControllerBase
{
    // Only the most recent incidents get an AI-generated narrative — bounds worst-case
    // latency to ~one Claude call's timeout instead of serial calls across the whole
    // historical backlog on every page load.
    private const int AiSummaryEnrichmentLimit = 10;

    private readonly MonitoringDbContext _db;
    private readonly ITargetProvider _targetProvider;
    private readonly IncidentAnalysisService _analysis;
    private readonly IServiceScopeFactory _scopeFactory;

    public HistoryController(MonitoringDbContext db, ITargetProvider targetProvider, IncidentAnalysisService analysis, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _targetProvider = targetProvider;
        _analysis = analysis;
        _scopeFactory = scopeFactory;
    }

    [HttpGet("incidents")]
    public async Task<ActionResult<IEnumerable<IncidentDto>>> Incidents(
        [FromQuery] string environment, [FromQuery] string? module, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environment)) return BadRequest(new { error = "environment is required." });

        // Excludes stale rows from the removed UI-check feature: those were stored
        // with CheckType=2, which no longer maps to a named enum member (Api=0/Db=1
        // only) and would otherwise serialize as a raw number instead of a string.
        var query = _db.StatusChecks.Where(c =>
            c.Environment == environment && (c.CheckType == CheckType.Api || c.CheckType == CheckType.Db));
        if (!string.IsNullOrWhiteSpace(module)) query = query.Where(c => c.ServiceName == module);

        var checks = await query.OrderBy(c => c.TimestampUtc).ToListAsync(ct);

        var incidents = checks
            .GroupBy(c => (c.ServiceName, c.CheckType))
            .SelectMany(g => _analysis.ComputeIncidents(g.Key.ServiceName, g.Key.CheckType, g.ToList()))
            .OrderByDescending(i => i.StartedAtUtc)
            .ToList();

        // Enrich only the most recent handful with an AI-generated narrative, in parallel.
        // Each branch resolves its own DI scope (and thus its own MonitoringDbContext) —
        // EF Core's DbContext is not thread-safe, so parallel branches must never share one
        // (sharing it throws "A second operation was started on this context instance").
        // Falls back to the existing template summary automatically (SummarizeAsync never
        // throws) when no AI key is configured or the call fails/times out.
        var toEnrich = incidents.Take(AiSummaryEnrichmentLimit).ToList();
        await Task.WhenAll(toEnrich.Select(async incident =>
        {
            using var scope = _scopeFactory.CreateScope();
            var scopedAnalysis = scope.ServiceProvider.GetRequiredService<IncidentAnalysisService>();
            var scopedClaude = scope.ServiceProvider.GetRequiredService<IClaudeService>();
            incident.Summary = await scopedAnalysis.SummarizeAsync(incident, scopedClaude, ct);
        }));

        return Ok(incidents);
    }

    [HttpGet("checks")]
    public async Task<ActionResult<IEnumerable<CheckBucketDto>>> Checks(
        [FromQuery] string environment, [FromQuery] string module, [FromQuery] CheckType checkType,
        [FromQuery] int hours, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(module))
        {
            return BadRequest(new { error = "environment and module are required." });
        }

        var window = TimeSpan.FromHours(Math.Clamp(hours <= 0 ? 24 : hours, 1, 168));
        var since = DateTime.UtcNow - window;

        var checks = await _db.StatusChecks
            .Where(c => c.Environment == environment && c.ServiceName == module && c.CheckType == checkType && c.TimestampUtc >= since)
            .OrderBy(c => c.TimestampUtc)
            .ToListAsync(ct);

        var bucketSize = TimeSpan.FromMinutes(Math.Max(5, window.TotalMinutes / 96));
        return Ok(_analysis.BucketChecks(checks, bucketSize));
    }

    [HttpGet("insights")]
    public async Task<ActionResult<IEnumerable<ModuleInsightDto>>> Insights(
        [FromQuery] string environment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environment)) return BadRequest(new { error = "environment is required." });

        var environments = await _targetProvider.GetEnvironmentsAsync(ct);
        var env = environments.FirstOrDefault(e => string.Equals(e.Name, environment, StringComparison.OrdinalIgnoreCase));
        if (env is null) return NotFound(new { error = "No such environment." });

        // 7 days is the full retention window — enough history for the
        // response-time baseline and the 24h-vs-previous-24h trend comparison.
        var since = DateTime.UtcNow.AddDays(-7);
        var allChecks = await _db.StatusChecks
            .Where(c => c.Environment == environment && c.TimestampUtc >= since
                && (c.CheckType == CheckType.Api || c.CheckType == CheckType.Db))
            .OrderBy(c => c.TimestampUtc)
            .ToListAsync(ct);

        var byModule = allChecks.ToLookup(c => c.ServiceName);

        var insights = env.Targets
            .Select(t => _analysis.ComputeInsight(t.Module, byModule[t.Module].ToList()))
            .ToList();

        return Ok(insights);
    }
}
