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

    private readonly MonitoringDbContext _db;
    private readonly ITargetProvider _targetProvider;
    private readonly IncidentAnalysisService _incidentAnalysis;
    private readonly PipelineAnalysisService _pipelineAnalysis;
    private readonly AzureDevOpsService _azure;
    private readonly IClaudeService _claude;
    private readonly SettingsService _settings;
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
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task EvaluateAndNotifyAsync(CancellationToken ct)
    {
        var enabled = string.Equals(await _settings.GetEffectiveAsync(SettingKeys.AlertsEnabled, ct), "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled)
        {
            return;
        }

        await EvaluateModulesAsync(ct);
        await EvaluatePipelinesAsync(ct);
    }

    private async Task EvaluateModulesAsync(CancellationToken ct)
    {
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

                if (openIncident is not null)
                {
                    var summary = await _incidentAnalysis.SummarizeAsync(openIncident, _claude, ct);
                    await FireIfNeededAsync(env.Name, target.Module, "incident", summary, ct);
                }
                else
                {
                    await ResolveIfOpenAsync(env.Name, target.Module, "incident", ct);
                }

                var insight = _incidentAnalysis.ComputeInsight(target.Module, moduleChecks);
                if (insight.IsFlaky)
                {
                    await FireIfNeededAsync(env.Name, target.Module, "flaky",
                        $"{target.Module} in {env.Name} is flaky — {insight.FlipsLastHour} state changes in the last hour.", ct);
                }
                else
                {
                    await ResolveIfOpenAsync(env.Name, target.Module, "flaky", ct);
                }

                if (insight.IsResponseTimeAnomalous)
                {
                    await FireIfNeededAsync(env.Name, target.Module, "anomaly",
                        $"{target.Module} in {env.Name} response time is anomalous — {insight.LatestResponseTimeMs}ms vs baseline {insight.BaselineResponseTimeMs}ms.", ct);
                }
                else
                {
                    await ResolveIfOpenAsync(env.Name, target.Module, "anomaly", ct);
                }
            }
        }
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
                var summary = await _pipelineAnalysis.SummarizeBuildAsync(insight, errors, _claude, ct);
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
