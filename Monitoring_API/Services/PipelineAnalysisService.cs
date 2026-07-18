using Monitoring_API.Models;

namespace Monitoring_API.Services;

// CI/CD equivalent of IncidentAnalysisService, applied to Azure DevOps build
// and release history instead of StatusChecks. Same techniques (flip
// counting, rolling mean/stddev z-score, period-over-period trend, gap
// duration between a failure and the next success) — no local persistence
// needed since Azure DevOps itself retains the history we read.
public class PipelineAnalysisService
{
    private const int FlakyFlipThreshold = 3;
    private const double AnomalyZScoreThreshold = 3.0;
    private const double TrendDegradingThresholdPoints = 3.0;
    private const int ConsecutiveFailureAlertThreshold = 3;

    public BuildInsightDto ComputeBuildInsight(int definitionId, string name, IReadOnlyList<BuildHistoryItem> builds)
    {
        var insight = new BuildInsightDto { DefinitionId = definitionId, Name = name };

        // Canceled builds carry no pass/fail signal — excluded from every rate/streak calc.
        var scored = builds.Where(b => b.Result != "canceled").ToList();
        if (scored.Count == 0) return insight;

        var now = scored[^1].FinishTime!.Value;

        var last7d = scored.Where(b => b.FinishTime >= now.AddDays(-7)).ToList();
        var prev7d = scored.Where(b => b.FinishTime >= now.AddDays(-14) && b.FinishTime < now.AddDays(-7)).ToList();
        var last30d = scored.Where(b => b.FinishTime >= now.AddDays(-30)).ToList();

        insight.SuccessRate7d = SuccessRate(last7d);
        insight.SuccessRate30d = SuccessRate(last30d);

        if (prev7d.Count > 0 && last7d.Count > 0)
        {
            var delta = insight.SuccessRate7d - SuccessRate(prev7d);
            insight.Trend = delta <= -TrendDegradingThresholdPoints ? "degrading"
                : delta >= TrendDegradingThresholdPoints ? "improving"
                : "stable";
        }

        // Flip counting over the whole fetched window (same technique as ModuleInsight flakiness).
        int flips = 0;
        for (int i = 1; i < scored.Count; i++)
        {
            if (IsSuccess(scored[i]) != IsSuccess(scored[i - 1])) flips++;
        }
        insight.FlipCount = flips;
        insight.IsFlaky = flips >= FlakyFlipThreshold;

        // Consecutive failures, most recent backwards.
        int streak = 0;
        for (int i = scored.Count - 1; i >= 0 && !IsSuccess(scored[i]); i--) streak++;
        insight.ConsecutiveFailures = streak;

        // Duration anomaly: baseline from successful builds with both timestamps, excluding the latest.
        var timed = scored.Where(b => IsSuccess(b) && b.StartTime is not null && b.FinishTime is not null).ToList();
        if (timed.Count >= 5)
        {
            var durations = timed.Select(b => (b.FinishTime!.Value - b.StartTime!.Value).TotalMinutes).ToList();
            var latest = durations[^1];
            var baseline = durations.Take(durations.Count - 1).ToList();
            var mean = baseline.Average();
            var stddev = Math.Sqrt(baseline.Select(v => Math.Pow(v - mean, 2)).Average());
            insight.LatestDurationMinutes = Math.Round(latest, 1);
            insight.BaselineDurationMinutes = Math.Round(mean, 1);
            if (stddev > 0)
            {
                insight.IsDurationAnomalous = (latest - mean) / stddev >= AnomalyZScoreThreshold;
            }
        }

        return insight;
    }

    public ReleaseInsightDto ComputeReleaseInsight(int definitionId, string name, IReadOnlyList<ReleaseHistoryItem> releases)
    {
        var insight = new ReleaseInsightDto { DefinitionId = definitionId, Name = name };
        if (releases.Count == 0) return insight;

        var now = releases[^1].CreatedOn!.Value;
        var windowDays = Math.Max(1, (now - releases[0].CreatedOn!.Value).TotalDays);

        var deployed = releases.Count(r => r.OverallStatus is "succeeded" or "partiallySucceeded");
        var failed = releases.Count(r => r.OverallStatus == "failed");

        insight.DeploymentsPerWeek = Math.Round(deployed / windowDays * 7, 1);
        insight.ChangeFailureRatePercent = Math.Round(100.0 * failed / releases.Count, 1);

        // MTTR: for every failure, find the next success and measure the gap; average across all pairs found.
        var recoveryGaps = new List<double>();
        DateTime? openFailureAt = null;
        foreach (var r in releases)
        {
            if (r.OverallStatus == "failed")
            {
                openFailureAt ??= r.CreatedOn;
            }
            else if (r.OverallStatus == "succeeded" && openFailureAt is not null)
            {
                var recoveredAt = r.ModifiedOn ?? r.CreatedOn!.Value;
                recoveryGaps.Add((recoveredAt - openFailureAt.Value).TotalMinutes);
                openFailureAt = null;
            }
        }
        insight.MttrMinutes = recoveryGaps.Count > 0 ? Math.Round(recoveryGaps.Average(), 1) : null;

        int streak = 0;
        for (int i = releases.Count - 1; i >= 0 && releases[i].OverallStatus == "failed"; i--) streak++;
        insight.ConsecutiveFailures = streak;

        return insight;
    }

    public static bool ShouldAlert(int consecutiveFailures) => consecutiveFailures >= ConsecutiveFailureAlertThreshold;

    private static string BuildTemplateSummary(BuildInsightDto insight) =>
        $"{insight.Name}: {insight.SuccessRate7d}% success (7d), " +
        $"{insight.ConsecutiveFailures} consecutive failure(s), trend {insight.Trend}" +
        (insight.IsFlaky ? ", flaky" : "") +
        (insight.IsDurationAnomalous ? ", duration anomaly detected" : "") + ".";

    // AI-enriched build insight narrative. Grounded in the already-computed statistical
    // flags plus the build's own error timeline (fetched by the caller) — falls back
    // silently to a plain template sentence when no Claude key is configured or the
    // call fails/times out.
    public async Task<string> SummarizeBuildAsync(BuildInsightDto insight, IReadOnlyList<BuildErrorRecordDto> errors, IClaudeService claude, CancellationToken ct = default)
    {
        var template = BuildTemplateSummary(insight);

        var errorLines = errors.Count == 0
            ? "none captured"
            : string.Join("; ", errors.SelectMany(e => e.Issues.Select(i => i.Message))
                .Where(m => !string.IsNullOrWhiteSpace(m)).Take(5));

        var context = $"""
            Pipeline: {insight.Name}
            Success rate (7d): {insight.SuccessRate7d}%
            Success rate (30d): {insight.SuccessRate30d}%
            Consecutive failures: {insight.ConsecutiveFailures}
            Flaky (flip count {insight.FlipCount} in window): {insight.IsFlaky}
            Trend: {insight.Trend}
            Duration anomaly: {insight.IsDurationAnomalous} (latest {insight.LatestDurationMinutes} min vs baseline {insight.BaselineDurationMinutes} min)
            Recent build error messages: {errorLines}
            """;

        var text = await claude.CompleteAsync(
            systemPrompt: "You are a DevOps assistant writing CI/CD pipeline health summaries for a monitoring " +
                          "dashboard. Given structured build-pipeline statistics and any captured error messages, " +
                          "write ONE concise plain-English sentence describing the pipeline's likely health issue " +
                          "and probable cause. Do not invent facts not present in the data. No preamble.",
            userPrompt: context,
            maxTokens: 200,
            ct: ct);

        return string.IsNullOrWhiteSpace(text) ? template : text.Trim();
    }

    private static bool IsSuccess(BuildHistoryItem b) => b.Result == "succeeded";

    private static double SuccessRate(IReadOnlyList<BuildHistoryItem> builds) =>
        builds.Count == 0 ? 0 : Math.Round(100.0 * builds.Count(IsSuccess) / builds.Count, 1);
}
