namespace Monitoring_API.Models;

// One contiguous down period for a (module, checkType) pair, derived from the
// raw StatusChecks history — this is the "history log" the dashboard has never
// exposed: when something went down, for how long, and why.
public class IncidentDto
{
    public string Module { get; set; } = string.Empty;
    public CheckType CheckType { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }   // null = still down
    public double DurationMinutes { get; set; }
    public string? ErrorMessage { get; set; }
    public int? HttpStatusCode { get; set; }
    public int SampleCount { get; set; }
    // Template-generated one-line summary — no LLM, pure string formatting
    // over the fields above (see IncidentAnalysisService.Summarize).
    public string Summary { get; set; } = string.Empty;
}

// One bucketed point for the status-timeline bar (majority-vote up/down per
// time bucket, same convention classic status pages use for their bars).
public class CheckBucketDto
{
    public DateTime BucketStartUtc { get; set; }
    public bool IsUp { get; set; }
    public int SampleCount { get; set; }
}

// Heuristic "AI" insight flags for one module — all computed from statistics
// over StatusChecks, no external AI service involved.
public class ModuleInsightDto
{
    public string Module { get; set; } = string.Empty;
    public bool IsFlaky { get; set; }
    public int FlipsLastHour { get; set; }
    public bool IsResponseTimeAnomalous { get; set; }
    public int? LatestResponseTimeMs { get; set; }
    public double? BaselineResponseTimeMs { get; set; }
    public string Trend { get; set; } = "stable"; // "improving" | "stable" | "degrading"
    public double UptimeLast24h { get; set; }
    public double UptimePrevious24h { get; set; }
}
