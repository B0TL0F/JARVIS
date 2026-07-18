using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Middleware;
using Monitoring_API.Models;
using Monitoring_API.Services;

namespace Monitoring_API.Controllers;

// Runtime settings page backend (Sentinel-style). Admin-only. Lets an admin set
// the Azure DevOps connection (incl. PAT) without a redeploy, and trigger an
// Ocelot re-import. The PAT is write-only: it is never returned by GET.
[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly SettingsService _settings;
    private readonly OcelotImportService _ocelot;
    private readonly ActivityLogger _activity;
    private readonly MonitoringDbContext _db;

    public SettingsController(SettingsService settings, OcelotImportService ocelot, ActivityLogger activity, MonitoringDbContext db)
    {
        _settings = settings;
        _ocelot = ocelot;
        _activity = activity;
        _db = db;
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
    public async Task<ActionResult<object>> Get(CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        return Ok(new
        {
            azureDevOps = new AzureDevOpsSettingsDto
            {
                Org = await _settings.GetEffectiveAsync(SettingKeys.AzureOrg, ct),
                Project = await _settings.GetEffectiveAsync(SettingKeys.AzureProject, ct),
                ApiVersion = await _settings.GetEffectiveAsync(SettingKeys.AzureApiVersion, ct),
                PatConfigured = await _settings.HasValueAsync(SettingKeys.AzurePat, ct)
            },
            ocelot = new
            {
                directory = _ocelot.OcelotDirectory
            },
            aiProvider = new AiProviderSettingsDto
            {
                Provider = await _settings.GetEffectiveAsync(SettingKeys.AiProvider, ct) ?? "claude"
            },
            claude = new ClaudeSettingsDto
            {
                Model = await _settings.GetEffectiveAsync(SettingKeys.ClaudeModel, ct) ?? "claude-opus-4-8",
                ApiKeyConfigured = await _settings.HasValueAsync(SettingKeys.ClaudeApiKey, ct)
            },
            gemini = new GeminiSettingsDto
            {
                Model = await _settings.GetEffectiveAsync(SettingKeys.GeminiModel, ct) ?? "gemini-2.0-flash",
                ApiKeyConfigured = await _settings.HasValueAsync(SettingKeys.GeminiApiKey, ct)
            },
            groq = new GroqSettingsDto
            {
                Model = await _settings.GetEffectiveAsync(SettingKeys.GroqModel, ct) ?? "llama-3.3-70b-versatile",
                ApiKeyConfigured = await _settings.HasValueAsync(SettingKeys.GroqApiKey, ct)
            },
            alerts = new AlertSettingsDto
            {
                Enabled = string.Equals(await _settings.GetEffectiveAsync(SettingKeys.AlertsEnabled, ct), "true", StringComparison.OrdinalIgnoreCase),
                TeamsWebhookConfigured = await _settings.HasValueAsync(SettingKeys.AlertsTeamsWebhookUrl, ct),
                SmtpHost = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpHost, ct),
                SmtpPort = int.TryParse(await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpPort, ct), out var port) ? port : null,
                SmtpUsername = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpUsername, ct),
                SmtpFrom = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpFrom, ct),
                SmtpTo = await _settings.GetEffectiveAsync(SettingKeys.AlertsSmtpTo, ct),
                SmtpPasswordConfigured = await _settings.HasValueAsync(SettingKeys.AlertsSmtpPassword, ct)
            },
            autoRemediation = new AutoRemediationSettingsDto
            {
                Enabled = string.Equals(await _settings.GetEffectiveAsync(SettingKeys.AutoRemediationEnabled, ct), "true", StringComparison.OrdinalIgnoreCase),
                // Defaults to true (fail toward safe) when unset — only an explicit "false" turns dry-run off.
                DryRun = !string.Equals(await _settings.GetEffectiveAsync(SettingKeys.AutoRemediationDryRun, ct), "false", StringComparison.OrdinalIgnoreCase)
            }
        });
    }

    [HttpPut("claude")]
    public async Task<IActionResult> UpdateClaude([FromBody] ClaudeSettingsUpdate req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        await _settings.SetAsync(SettingKeys.ClaudeModel, string.IsNullOrWhiteSpace(req.Model) ? null : req.Model.Trim(), ct);

        // Only overwrite the API key when a new one is supplied — blank means "keep existing".
        if (!string.IsNullOrWhiteSpace(req.ApiKey))
        {
            await _settings.SetAsync(SettingKeys.ClaudeApiKey, req.ApiKey.Trim(), ct);
        }

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "settings.claude", "Updated Claude AI settings", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    [HttpPut("gemini")]
    public async Task<IActionResult> UpdateGemini([FromBody] GeminiSettingsUpdate req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        await _settings.SetAsync(SettingKeys.GeminiModel, string.IsNullOrWhiteSpace(req.Model) ? null : req.Model.Trim(), ct);

        // Only overwrite the API key when a new one is supplied — blank means "keep existing".
        if (!string.IsNullOrWhiteSpace(req.ApiKey))
        {
            await _settings.SetAsync(SettingKeys.GeminiApiKey, req.ApiKey.Trim(), ct);
        }

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "settings.gemini", "Updated Gemini AI settings", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    [HttpPut("groq")]
    public async Task<IActionResult> UpdateGroq([FromBody] GroqSettingsUpdate req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        await _settings.SetAsync(SettingKeys.GroqModel, string.IsNullOrWhiteSpace(req.Model) ? null : req.Model.Trim(), ct);

        // Only overwrite the API key when a new one is supplied — blank means "keep existing".
        if (!string.IsNullOrWhiteSpace(req.ApiKey))
        {
            await _settings.SetAsync(SettingKeys.GroqApiKey, req.ApiKey.Trim(), ct);
        }

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "settings.groq", "Updated Groq AI settings", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    [HttpPut("ai-provider")]
    public async Task<IActionResult> UpdateAiProvider([FromBody] AiProviderSettingsUpdate req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var provider = req.Provider?.ToLowerInvariant() switch
        {
            "gemini" => "gemini",
            "groq" => "groq",
            _ => "claude"
        };
        await _settings.SetAsync(SettingKeys.AiProvider, provider, ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "settings.ai-provider", $"Switched AI provider to '{provider}'", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    [HttpPut("alerts")]
    public async Task<IActionResult> UpdateAlerts([FromBody] AlertSettingsUpdate req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        await _settings.SetAsync(SettingKeys.AlertsEnabled, req.Enabled ? "true" : "false", ct);
        await _settings.SetAsync(SettingKeys.AlertsSmtpHost, string.IsNullOrWhiteSpace(req.SmtpHost) ? null : req.SmtpHost.Trim(), ct);
        await _settings.SetAsync(SettingKeys.AlertsSmtpPort, req.SmtpPort?.ToString(), ct);
        await _settings.SetAsync(SettingKeys.AlertsSmtpUsername, string.IsNullOrWhiteSpace(req.SmtpUsername) ? null : req.SmtpUsername.Trim(), ct);
        await _settings.SetAsync(SettingKeys.AlertsSmtpFrom, string.IsNullOrWhiteSpace(req.SmtpFrom) ? null : req.SmtpFrom.Trim(), ct);
        await _settings.SetAsync(SettingKeys.AlertsSmtpTo, string.IsNullOrWhiteSpace(req.SmtpTo) ? null : req.SmtpTo.Trim(), ct);

        // Only overwrite secrets when a new value is supplied — blank means "keep existing".
        if (!string.IsNullOrWhiteSpace(req.TeamsWebhookUrl))
        {
            await _settings.SetAsync(SettingKeys.AlertsTeamsWebhookUrl, req.TeamsWebhookUrl.Trim(), ct);
        }
        if (!string.IsNullOrWhiteSpace(req.SmtpPassword))
        {
            await _settings.SetAsync(SettingKeys.AlertsSmtpPassword, req.SmtpPassword.Trim(), ct);
        }

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "settings.alerts", "Updated alert delivery settings", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    [HttpPut("azure-devops")]
    public async Task<IActionResult> UpdateAzure([FromBody] AzureDevOpsSettingsUpdate req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        await _settings.SetAsync(SettingKeys.AzureOrg, req.Org?.Trim(), ct);
        await _settings.SetAsync(SettingKeys.AzureProject, req.Project?.Trim(), ct);
        await _settings.SetAsync(SettingKeys.AzureApiVersion, string.IsNullOrWhiteSpace(req.ApiVersion) ? null : req.ApiVersion.Trim(), ct);

        // Only overwrite the PAT when a new one is supplied — blank means "keep existing".
        if (!string.IsNullOrWhiteSpace(req.Pat))
        {
            await _settings.SetAsync(SettingKeys.AzurePat, req.Pat.Trim(), ct);
        }

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "settings.azure", "Updated Azure DevOps settings", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    [HttpPut("auto-remediation")]
    public async Task<IActionResult> UpdateAutoRemediation([FromBody] AutoRemediationSettingsUpdate req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        await _settings.SetAsync(SettingKeys.AutoRemediationEnabled, req.Enabled ? "true" : "false", ct);
        await _settings.SetAsync(SettingKeys.AutoRemediationDryRun, req.DryRun ? "true" : "false", ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "settings.auto-remediation", $"Updated auto-remediation settings (enabled={req.Enabled}, dryRun={req.DryRun})",
            BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    [HttpGet("remediation-rules")]
    public async Task<ActionResult<List<RemediationRuleDto>>> GetRemediationRules(CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var rules = await _db.RemediationRules
            .OrderBy(r => r.Environment).ThenBy(r => r.Module)
            .Select(r => new RemediationRuleDto
            {
                Id = r.Id,
                Environment = r.Environment,
                Module = r.Module,
                AzureDevOpsDefinitionId = r.AzureDevOpsDefinitionId,
                Branch = r.Branch,
                Enabled = r.Enabled,
                MaxActionsPerHour = r.MaxActionsPerHour,
                UpdatedAtUtc = r.UpdatedAtUtc,
                UpdatedBy = r.UpdatedBy
            })
            .ToListAsync(ct);

        return Ok(rules);
    }

    [HttpPut("remediation-rules")]
    public async Task<ActionResult<RemediationRuleDto>> UpsertRemediationRule([FromBody] RemediationRuleUpsertDto req, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        if (string.IsNullOrWhiteSpace(req.Environment) || string.IsNullOrWhiteSpace(req.Module) || req.AzureDevOpsDefinitionId <= 0)
        {
            return BadRequest(new { error = "Environment, module, and a valid Azure DevOps definition ID are required." });
        }

        var rule = await _db.RemediationRules.FirstOrDefaultAsync(
            r => r.Environment == req.Environment && r.Module == req.Module, ct);

        if (rule is null)
        {
            rule = new RemediationRule { Environment = req.Environment.Trim(), Module = req.Module.Trim() };
            _db.RemediationRules.Add(rule);
        }

        rule.AzureDevOpsDefinitionId = req.AzureDevOpsDefinitionId;
        rule.Branch = string.IsNullOrWhiteSpace(req.Branch) ? null : req.Branch.Trim();
        rule.Enabled = req.Enabled;
        rule.MaxActionsPerHour = req.MaxActionsPerHour <= 0 ? 1 : req.MaxActionsPerHour;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedBy = CurrentUser.Name(User);

        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User), "settings.remediation-rule",
            $"Upserted remediation rule for {rule.Environment}/{rule.Module} -> definition {rule.AzureDevOpsDefinitionId}",
            BasicAuthMiddleware.ClientIp(HttpContext));

        return Ok(new RemediationRuleDto
        {
            Id = rule.Id,
            Environment = rule.Environment,
            Module = rule.Module,
            AzureDevOpsDefinitionId = rule.AzureDevOpsDefinitionId,
            Branch = rule.Branch,
            Enabled = rule.Enabled,
            MaxActionsPerHour = rule.MaxActionsPerHour,
            UpdatedAtUtc = rule.UpdatedAtUtc,
            UpdatedBy = rule.UpdatedBy
        });
    }

    [HttpDelete("remediation-rules/{id:int}")]
    public async Task<IActionResult> DeleteRemediationRule(int id, CancellationToken ct)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var rule = await _db.RemediationRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return NotFound();

        _db.RemediationRules.Remove(rule);
        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User), "settings.remediation-rule",
            $"Deleted remediation rule for {rule.Environment}/{rule.Module}", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    [HttpPost("ocelot/reimport")]
    public async Task<ActionResult<object>> ReimportOcelot([FromQuery] bool force = false, CancellationToken ct = default)
    {
        if (!RequireAdmin(out var forbid)) return forbid!;

        var result = await _ocelot.ReimportAllAsync(force, ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "settings.ocelot.reimport", force ? "Forced Ocelot re-import" : "Ocelot re-import",
            BasicAuthMiddleware.ClientIp(HttpContext));

        return Ok(result);
    }
}
