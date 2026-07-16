using Monitoring_API.Models;

namespace Monitoring_API.Services;

// Turns the raw StatusChecks stream into two things no part of Jarvis has
// ever exposed before:
//   1. An incident/history log — when something went down, for how long, why.
//   2. Heuristic "AI" insights — anomaly/flaky/trend flags computed with
//      plain statistics (rolling mean/stddev, transition counting, period
//      comparison). No external AI service is involved or needed; this is
//      the same class of technique real APM tools use under the hood.
public class IncidentAnalysisService
{
    // A check "flips" state if isUp differs from 3+ times in the last hour,
    // it's flagged flaky even though the current sample may show "up".
    private const int FlakyFlipThreshold = 3;

    // Anomalous if the latest response time exceeds this many standard
    // deviations above the module's own recent baseline.
    private const double AnomalyZScoreThreshold = 3.0;

    // A period-over-period uptime drop of at least this many points is
    // reported as a "degrading" trend rather than noise.
    private const double TrendDegradingThresholdPoints = 3.0;

    // Groups consecutive IsUp=false runs into incidents. `checks` must
    // already be filtered to one (module, checkType) pair and ordered by
    // TimestampUtc ascending.
    public List<IncidentDto> ComputeIncidents(string module, CheckType checkType, IReadOnlyList<StatusCheck> checks)
    {
        var incidents = new List<IncidentDto>();
        if (checks.Count == 0) return incidents;

        StatusCheck? runStart = null;
        StatusCheck? runLast = null;
        int runCount = 0;

        void CloseRun(DateTime? resolvedAt)
        {
            if (runStart is null) return;
            var duration = ((resolvedAt ?? runLast!.TimestampUtc) - runStart.TimestampUtc).TotalMinutes;
            var incident = new IncidentDto
            {
                Module = module,
                CheckType = checkType,
                StartedAtUtc = runStart.TimestampUtc,
                ResolvedAtUtc = resolvedAt,
                DurationMinutes = Math.Round(Math.Max(duration, 0), 1),
                ErrorMessage = runStart.ErrorMessage,
                HttpStatusCode = runStart.HttpStatusCode,
                SampleCount = runCount
            };
            incident.Summary = Summarize(incident, incidents.Count);
            incidents.Add(incident);
            runStart = null;
            runLast = null;
            runCount = 0;
        }

        foreach (var check in checks)
        {
            if (!check.IsUp)
            {
                runStart ??= check;
                runLast = check;
                runCount++;
            }
            else if (runStart is not null)
            {
                // The recovering "up" check's timestamp marks when it resolved.
                CloseRun(check.TimestampUtc);
            }
        }

        // Still down as of the most recent check — leave ResolvedAtUtc null.
        if (runStart is not null) CloseRun(null);

        return incidents.OrderByDescending(i => i.StartedAtUtc).ToList();
    }

    // IST is a fixed +05:30 offset with no daylight saving, so a plain TimeSpan
    // add is correct and avoids depending on the container having IANA tzdata
    // for a TimeZoneInfo lookup.
    private static readonly TimeSpan IstOffset = new(5, 30, 0);

    private static string Summarize(IncidentDto incident, int priorIncidentCountThisBatch)
    {
        var checkLabel = incident.CheckType == CheckType.Api ? "API check" : "DB check";
        var reason = string.IsNullOrWhiteSpace(incident.ErrorMessage)
            ? $"non-2xx response{(incident.HttpStatusCode is int code ? $" ({code})" : "")}"
            : incident.ErrorMessage;

        var startedIst = incident.StartedAtUtc + IstOffset;

        if (incident.ResolvedAtUtc is null)
        {
            return $"{incident.Module} has been down for {FormatDuration(incident.DurationMinutes)} " +
                   $"(since {startedIst:HH:mm} IST) — {reason} on the {checkLabel}.";
        }

        var resolvedIst = incident.ResolvedAtUtc.Value + IstOffset;
        return $"{incident.Module} was down for {FormatDuration(incident.DurationMinutes)} " +
               $"({startedIst:HH:mm}–{resolvedIst:HH:mm} IST) — {reason} on the {checkLabel}.";
    }

    private static string FormatDuration(double minutes)
    {
        if (minutes < 1) return "under a minute";
        if (minutes < 60) return $"{Math.Round(minutes)} min";
        var hours = minutes / 60.0;
        return hours < 24 ? $"{hours:0.#} hr" : $"{hours / 24:0.#} days";
    }

    // Buckets an ordered check series into fixed-size time windows, majority
    // vote per bucket — the same rendering convention classic status-page
    // timeline bars use.
    public List<CheckBucketDto> BucketChecks(IReadOnlyList<StatusCheck> checks, TimeSpan bucketSize)
    {
        var buckets = new List<CheckBucketDto>();
        if (checks.Count == 0) return buckets;

        var groups = checks.GroupBy(c =>
        {
            var ticks = c.TimestampUtc.Ticks - (c.TimestampUtc.Ticks % bucketSize.Ticks);
            return new DateTime(ticks, DateTimeKind.Utc);
        });

        foreach (var g in groups.OrderBy(g => g.Key))
        {
            var total = g.Count();
            var upCount = g.Count(c => c.IsUp);
            buckets.Add(new CheckBucketDto
            {
                BucketStartUtc = g.Key,
                IsUp = upCount * 2 >= total, // majority vote
                SampleCount = total
            });
        }

        return buckets;
    }

    // `allChecksForModule` should span at least the last 7 days across both
    // check types, ordered by TimestampUtc ascending, for one module.
    public ModuleInsightDto ComputeInsight(string module, IReadOnlyList<StatusCheck> allChecksForModule)
    {
        var insight = new ModuleInsightDto { Module = module };
        if (allChecksForModule.Count == 0) return insight;

        var now = allChecksForModule[^1].TimestampUtc;

        // Flakiness: count isUp transitions within the last hour.
        var lastHour = allChecksForModule.Where(c => c.TimestampUtc >= now.AddHours(-1)).ToList();
        int flips = 0;
        for (int i = 1; i < lastHour.Count; i++)
        {
            if (lastHour[i].IsUp != lastHour[i - 1].IsUp) flips++;
        }
        insight.FlipsLastHour = flips;
        insight.IsFlaky = flips >= FlakyFlipThreshold;

        // Response-time anomaly: baseline from successful checks excluding the
        // most recent sample, compared against that latest sample.
        var successful = allChecksForModule.Where(c => c.IsUp && c.ResponseTimeMs is not null).ToList();
        if (successful.Count >= 5)
        {
            var latest = successful[^1];
            var baseline = successful.Take(successful.Count - 1).Select(c => (double)c.ResponseTimeMs!.Value).ToList();
            var mean = baseline.Average();
            var stddev = Math.Sqrt(baseline.Select(v => Math.Pow(v - mean, 2)).Average());
            insight.LatestResponseTimeMs = latest.ResponseTimeMs;
            insight.BaselineResponseTimeMs = Math.Round(mean, 0);
            if (stddev > 0)
            {
                var z = (latest.ResponseTimeMs!.Value - mean) / stddev;
                insight.IsResponseTimeAnomalous = z >= AnomalyZScoreThreshold;
            }
        }

        // Trend: last 24h uptime% vs. the 24h before that.
        var last24h = allChecksForModule.Where(c => c.TimestampUtc >= now.AddHours(-24)).ToList();
        var prev24h = allChecksForModule.Where(c => c.TimestampUtc >= now.AddHours(-48) && c.TimestampUtc < now.AddHours(-24)).ToList();
        insight.UptimeLast24h = UptimePercent(last24h);
        insight.UptimePrevious24h = UptimePercent(prev24h);
        if (prev24h.Count > 0 && last24h.Count > 0)
        {
            var delta = insight.UptimeLast24h - insight.UptimePrevious24h;
            insight.Trend = delta <= -TrendDegradingThresholdPoints ? "degrading"
                : delta >= TrendDegradingThresholdPoints ? "improving"
                : "stable";
        }

        return insight;
    }

    private static double UptimePercent(IReadOnlyList<StatusCheck> checks) =>
        checks.Count == 0 ? 0 : Math.Round(100.0 * checks.Count(c => c.IsUp) / checks.Count, 1);
}
