using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

// Watches the existing (previously undelivered) flaky/anomaly/incident/build-failure
// signals and, when one fires, sends a Teams webhook message and/or email — at most
// once per ongoing condition (dedup via AlertHistory), with an occasional reminder for
// very long-running incidents. Fully inert (zero DB writes, zero HTTP calls) unless an
// admin has explicitly turned Alerts on in Settings.
public class AlertNotificationService
{
    // For an incident/condition that stays open a long time, re-notify at most this often
    // rather than firing exactly once and going silent for the rest of the outage.
    private static readonly TimeSpan ReminderCooldown = TimeSpan.FromHours(4);

    // Auto-remediation: don't re-trigger a build for the same ongoing condition more often
    // than this, even if under the per-rule hourly cap — catches "just triggered 5 min ago,
    // still failing" distinctly from a pure per-hour counter.
    private static readonly TimeSpan RemediationCooldown = TimeSpan.FromMinutes(15);

    private readonly MonitoringDbContext _db;
    private readonly ITargetProvider _targetProvider;
    private readonly IncidentAnalysisService _incidentAnalysis;
    private readonly PipelineAnalysisService _pipelineAnalysis;
    private readonly AzureDevOpsService _azure;
    private readonly IClaudeService _claude;
    private readonly SettingsService _settings;
    private readonly ActivityLogger _activity;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AlertNotificationService> _logger;

    public AlertNotificationService(
        MonitoringDbContext db,
        ITargetProvider targetProvider,
        IncidentAnalysisService incidentAnalysis,
        PipelineAnalysisService pipelineAnalysis,
        AzureDevOpsService azure,
        IClaudeService claude,
        SettingsService settings,
        ActivityLogger activity,
        IHttpClientFactory httpClientFactory,
        ILogger<AlertNotificationService> logger)
    {
        _db = db;
        _targetProvider = targetProvider;
        _incidentAnalysis = incidentAnalysis;
        _pipelineAnalysis = pipelineAnalysis;
        _azure = azure;
        _claude = claude;
        _settings = settings;
        _activity = activity;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task EvaluateAndNotifyAsync(CancellationToken ct)
    {
        var enabled = string.Equals(await _settings.GetEffectiveAsync(SettingKeys.AlertsEnabled, ct), "true", StringComparison.OrdinalIgnoreCase);
        if (enabled)
        {
            var results = await EvaluateModulesAsync(ct);
            await EvaluateRemediationAsync(results, ct);
            await EvaluatePipelinesAsync(ct);
        }
        else
        {
            // Alerts off doesn't necessarily mean remediation should be skipped — remediation
            // has its own master switch — but it needs the same incident/insight computation,
            // so recompute it here if remediation alone is enabled.
            var remediationEnabled = string.Equals(await _settings.GetEffectiveAsync(SettingKeys.AutoRemediationEnabled, ct), "true", StringComparison.OrdinalIgnoreCase);
            if (remediationEnabled)
            {
                var results = await ComputeModuleResultsAsync(ct);
                await EvaluateRemediationAsync(results, ct);
            }
        }
    }

    private async Task<List<ModuleEvalResult>> EvaluateModulesAsync(CancellationToken ct)
    {
        var results = await ComputeModuleResultsAsync(ct);

        foreach (var r in results)
        {
            if (r.OpenIncident is not null)
            {
                var summary = await _incidentAnalysis.SummarizeAsync(r.OpenIncident, _claude, ct);
                await FireIfNeededAsync(r.Environment, r.Module, "incident", summary, ct);
            }
            else
            {
                await ResolveIfOpenAsync(r.Environment, r.Module, "incident", ct);
            }

            if (r.Insight.IsFlaky)
            {
                await FireIfNeededAsync(r.Environment, r.Module, "flaky",
                    $"{r.Module} in {r.Environment} is flaky — {r.Insight.FlipsLastHour} state changes in the last hour.", ct);
            }
            else
            {
                await ResolveIfOpenAsync(r.Environment, r.Module, "flaky", ct);
            }

            if (r.Insight.IsResponseTimeAnomalous)
            {
                await FireIfNeededAsync(r.Environment, r.Module, "anomaly",
                    $"{r.Module} in {r.Environment} response time is anomalous — {r.Insight.LatestResponseTimeMs}ms vs baseline {r.Insight.BaselineResponseTimeMs}ms.", ct);
            }
            else
            {
                await ResolveIfOpenAsync(r.Environment, r.Module, "anomaly", ct);
            }
        }

        return results;
    }

    private sealed record ModuleEvalResult(string Environment, string Module, IncidentDto? OpenIncident, ModuleInsightDto Insight);

    // Loads checks and computes incidents/insight per module once per cycle — shared by both
    // the alert-firing loop above and the auto-remediation evaluation below, so the two never
    // recompute (and potentially disagree about) "is this module currently down".
    private async Task<List<ModuleEvalResult>> ComputeModuleResultsAsync(CancellationToken ct)
    {
        var results = new List<ModuleEvalResult>();
        var environments = await _targetProvider.GetEnvironmentsAsync(ct);
        var since = DateTime.UtcNow.AddDays(-7);

        foreach (var env in environments)
        {
            var allChecks = await _db.StatusChecks
                .Where(c => c.Environment == env.Name && c.TimestampUtc >= since
                    && (c.CheckType == CheckType.Api || c.CheckType == CheckType.Db))
                .OrderBy(c => c.TimestampUtc)
                .ToListAsync(ct);

            var byModule = allChecks.ToLookup(c => c.ServiceName);

            foreach (var target in env.Targets)
            {
                var moduleChecks = byModule[target.Module].ToList();
                if (moduleChecks.Count == 0) continue;

                // Open incident (still down as of the latest check)?
                var openIncident = moduleChecks
                    .GroupBy(c => c.CheckType)
                    .SelectMany(g => _incidentAnalysis.ComputeIncidents(target.Module, g.Key, g.ToList()))
                    .FirstOrDefault(i => i.ResolvedAtUtc is null);

                var insight = _incidentAnalysis.ComputeInsight(target.Module, moduleChecks);
                results.Add(new ModuleEvalResult(env.Name, target.Module, openIncident, insight));
            }
        }

        return results;
    }

    private async Task EvaluatePipelinesAsync(CancellationToken ct)
    {
        var dashboard = await _azure.GetPipelineDashboardAsync(ct);
        if (!dashboard.Ok) return;

        foreach (var def in dashboard.BuildPipelines.Where(d => d.RecentBuilds.Count > 0))
        {
            var history = await _azure.GetBuildHistoryAsync(def.Id, days: 30, ct);
            var insight = _pipelineAnalysis.ComputeBuildInsight(def.Id, def.Name, history);

            if (PipelineAnalysisService.ShouldAlert(insight.ConsecutiveFailures))
            {
                var errors = def.LatestBuildId is int bid ? await _azure.GetBuildErrorsAsync(bid, ct) : new List<BuildErrorRecordDto>();

                // Up to 2 other recent failed builds (excluding the current one) for "does this
                // look like a repeat of a past failure" comparison — cheap since history is
                // already fetched above, just reusing build IDs already in hand.
                var otherFailedBuildIds = history
                    .Where(b => b.Result is "failed" or "partiallySucceeded" && b.Id != def.LatestBuildId)
                    .OrderByDescending(b => b.FinishTime)
                    .Take(2)
                    .Select(b => b.Id)
                    .ToList();
                var pastFailureSnippets = new List<string>();
                foreach (var pastBuildId in otherFailedBuildIds)
                {
                    var pastErrors = await _azure.GetBuildErrorsAsync(pastBuildId, ct);
                    var msg = pastErrors.SelectMany(e => e.Issues.Select(i => i.Message))
                        .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
                    if (msg is not null) pastFailureSnippets.Add(msg);
                }

                var summary = await _pipelineAnalysis.SummarizeBuildAsync(insight, errors, pastFailureSnippets, _claude, ct);
                await FireIfNeededAsync("Pipelines", def.Name, "build_failure", summary, ct);
            }
            else
            {
                await ResolveIfOpenAsync("Pipelines", def.Name, "build_failure", ct);
            }
        }
    }

    private async Task FireIfNeededAsync(string environment, string module, string alertType, string summary, CancellationToken ct)
    {
        var dedupKey = $"{environment}:{module}:{alertType}";
        var now = DateTime.UtcNow;

        var open = await _db.AlertHistories.FirstOrDefaultAsync(a => a.DedupKey == dedupKey && a.ResolvedAtUtc == null, ct);

        if (open is null)
        {
            var row = new AlertHistory
            {
                Environment = environment,
                Module = module,
                AlertType = alertType,
                DedupKey = dedupKey,
                FirstFiredAtUtc = now,
                LastFiredAtUtc = now,
                FireCount = 1,
                Summary = summary
            };
            _db.AlertHistories.Add(row);
            await _db.SaveChangesAsync(ct);
            await SendAsync(row, ct);
            return;
        }

        // Still open — only re-notify if the reminder cooldown has elapsed.
        var dueForReminder = now - open.LastFiredAtUtc >= ReminderCooldown;
        open.FireCount++;
        open.LastFiredAtUtc = now;
        open.Summary = summary;
        await _db.SaveChangesAsync(ct);

        if (dueForReminder)
        {
            await SendAsync(open, ct);
        }
    }

    private async Task ResolveIfOpenAsync(string environment, string module, string alertType, CancellationToken ct)
    {
        var dedupKey = $"{environment}:{module}:{alertType}";
        var open = await _db.AlertHistories.FirstOrDefaultAsync(a => a.DedupKey == dedupKey && a.ResolvedAtUtc == null, ct);
        if (open is not null)
        {
            open.ResolvedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }

    // Auto-remediation: for each module still down, look up an explicit RemediationRule and —
    // if enabled, under the rate limit, and past cooldown — retrigger its build pipeline.
    // Off by default (AutoRemediation:Enabled), and defaults to dry-run (logs the decision
    // without calling Azure DevOps) even when enabled, so it can be observed safely first.
    private async Task EvaluateRemediationAsync(List<ModuleEvalResult> results, CancellationToken ct)
    {
        var enabled = string.Equals(await _settings.GetEffectiveAsync(SettingKeys.AutoRemediationEnabled, ct), "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled)
        {
            return;
        }

        var dryRun = !string.Equals(await _settings.GetEffectiveAsync(SettingKeys.AutoRemediationDryRun, ct), "false", StringComparison.OrdinalIgnoreCase);

        foreach (var r in results)
        {
            var dedupKey = $"{r.Environment}:{r.Module}:remediation";

            if (r.OpenIncident is null)
            {
                // Recovered — close any open remediation row so a future recurrence starts fresh.
                var openRow = await _db.RemediationHistories.FirstOrDefaultAsync(h => h.DedupKey == dedupKey && h.ResolvedAtUtc == null, ct);
                if (openRow is not null)
                {
                    openRow.ResolvedAtUtc = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
                continue;
            }

            var rule = await _db.RemediationRules.FirstOrDefaultAsync(
                x => x.Environment == r.Environment && x.Module == r.Module && x.Enabled, ct);
            if (rule is null)
            {
                // No explicit mapping (or explicitly disabled) — never fuzzy-matched, just skip.
                continue;
            }

            var now = DateTime.UtcNow;
            var hourAgo = now.AddHours(-1);
            var actionsThisHour = await _db.RemediationHistories.CountAsync(
                h => h.DedupKey == dedupKey && h.FiredAtUtc >= hourAgo && (h.Action == "triggered" || h.Action == "would_trigger"), ct);

            if (actionsThisHour >= rule.MaxActionsPerHour)
            {
                await LogRemediationAsync(r, rule, dedupKey, "skipped_rate_limit", ok: false, buildId: null, error: null, ct);
                continue;
            }

            var recentlyFired = await _db.RemediationHistories.AnyAsync(
                h => h.DedupKey == dedupKey && h.FiredAtUtc >= now.Subtract(RemediationCooldown)
                     && (h.Action == "triggered" || h.Action == "would_trigger"), ct);
            if (recentlyFired)
            {
                await LogRemediationAsync(r, rule, dedupKey, "skipped_cooldown", ok: false, buildId: null, error: null, ct);
                continue;
            }

            if (dryRun)
            {
                _logger.LogInformation("[DRY RUN] would trigger build {DefinitionId} for {Environment}/{Module}",
                    rule.AzureDevOpsDefinitionId, r.Environment, r.Module);
                await LogRemediationAsync(r, rule, dedupKey, "would_trigger", ok: true, buildId: null, error: null, ct);
                await _activity.LogAsync(null, "auto-remediation", "remediation.dry_run",
                    $"env={r.Environment} module={r.Module} definitionId={rule.AzureDevOpsDefinitionId} (dry run — no build queued)", null);
                continue;
            }

            var result = await _azure.QueueBuildAsync(rule.AzureDevOpsDefinitionId, rule.Branch, ct);
            await LogRemediationAsync(r, rule, dedupKey, "triggered", result.Ok, result.BuildId, result.Error, ct);
            await _activity.LogAsync(null, "auto-remediation", "remediation.trigger",
                $"env={r.Environment} module={r.Module} definitionId={rule.AzureDevOpsDefinitionId} buildId={result.BuildId} ok={result.Ok}" +
                (result.Ok ? "" : $" error={result.Error}"), null);
        }
    }

    private async Task LogRemediationAsync(ModuleEvalResult r, RemediationRule rule, string dedupKey, string action, bool ok, int? buildId, string? error, CancellationToken ct)
    {
        _db.RemediationHistories.Add(new RemediationHistory
        {
            Environment = r.Environment,
            Module = r.Module,
            DedupKey = dedupKey,
            RuleId = rule.Id,
            Action = action,
            Ok = ok,
            BuildId = buildId,
            Error = error,
            FiredAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task SendAsync(AlertHistory alert, CancellationToken ct)
    {
        var webhookUrl = await _settings.GetEffectiveAsync(SettingKeys.AlertsTeamsWebhookUrl, ct);
        if (!string.IsNullOrWhiteSpace(webhookUrl))
        {
            try
            {
                await SendTeamsAsync(webhookUrl, alert, ct);
                alert.TeamsSent = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Teams alert for {DedupKey}", alert.DedupKey);
            }
        }

        var smtpHost = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpHost, ct);
        if (!string.IsNullOrWhiteSpace(smtpHost))
        {
            try
            {
                await SendEmailAsync(alert, ct);
                alert.EmailSent = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send email alert for {DedupKey}", alert.DedupKey);
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task SendTeamsAsync(string webhookUrl, AlertHistory alert, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("teams");
        var card = new
        {
            title = $"Jarvis alert: {alert.Module} ({alert.AlertType})",
            text = alert.Summary ?? "An alert condition was detected."
        };
        var response = await client.PostAsJsonAsync(webhookUrl, card, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendEmailAsync(AlertHistory alert, CancellationToken ct)
    {
        var host = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpHost, ct);
        var portStr = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpPort, ct);
        var username = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpUsername, ct);
        var password = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpPassword, ct);
        var from = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpFrom, ct);
        var to = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpTo, ct);

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return;
        }

        var port = int.TryParse(portStr, out var p) ? p : 587;

        using var client = new SmtpClient(host, port);
        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password);
        }
        client.EnableSsl = true;

        using var message = new MailMessage
        {
            From = new MailAddress(from),
            Subject = $"Jarvis alert: {alert.Module} ({alert.AlertType})",
            Body = alert.Summary ?? "An alert condition was detected."
        };
        foreach (var recipient in to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.To.Add(recipient);
        }

        await client.SendMailAsync(message, ct);
    }
}
