namespace Monitoring_API.Models;

// Simple key/value store for runtime-configurable settings (Sentinel-style
// settings page). Values set here take precedence over environment variables,
// so the Azure DevOps PAT etc. can be changed without redeploying.
public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public static class SettingKeys
{
    public const string AzureOrg = "AzureDevOps:Org";
    public const string AzureProject = "AzureDevOps:Project";
    public const string AzurePat = "AzureDevOps:Pat";
    public const string AzureApiVersion = "AzureDevOps:ApiVersion";

    public const string ClaudeApiKey = "Claude:ApiKey";
    public const string ClaudeModel = "Claude:Model";

    // Which backend every AI feature (narratives, alerts, chat, trigger-tool) calls through.
    // Value is "claude", "gemini", or "groq" — defaults to "claude" when unset.
    public const string AiProvider = "Ai:Provider";

    public const string GeminiApiKey = "Gemini:ApiKey";
    public const string GeminiModel = "Gemini:Model";

    public const string GroqApiKey = "Groq:ApiKey";
    public const string GroqModel = "Groq:Model";

    public const string AlertsEnabled = "Alerts:Enabled";
    public const string AlertsTeamsWebhookUrl = "Alerts:TeamsWebhookUrl";
    public const string AlertsSmtpHost = "Alerts:Smtp:Host";
    public const string AlertsSmtpPort = "Alerts:Smtp:Port";
    public const string AlertsSmtpUsername = "Alerts:Smtp:Username";
    public const string AlertsSmtpPassword = "Alerts:Smtp:Password";
    public const string AlertsSmtpFrom = "Alerts:Smtp:From";
    public const string AlertsSmtpTo = "Alerts:Smtp:To";

    // Master switch for auto-remediation — default "false" (fail-safe off). Per-rule mapping
    // (which pipeline fixes which environment/module) lives in RemediationRule rows, not here.
    public const string AutoRemediationEnabled = "AutoRemediation:Enabled";
    // Default "true" (fail toward safe) — logs what WOULD be triggered without calling Azure DevOps.
    public const string AutoRemediationDryRun = "AutoRemediation:DryRun";
}

// What the settings page shows/saves for Azure DevOps. The PAT is write-only:
// GET returns PatConfigured (a bool) but never the secret itself.
public class AzureDevOpsSettingsDto
{
    public string? Org { get; set; }
    public string? Project { get; set; }
    public string? ApiVersion { get; set; }
    public bool PatConfigured { get; set; }
}

public class AzureDevOpsSettingsUpdate
{
    public string? Org { get; set; }
    public string? Project { get; set; }
    public string? ApiVersion { get; set; }
    // Only applied when non-empty — leaving it blank keeps the existing PAT.
    public string? Pat { get; set; }
}

// Claude API key is write-only: GET returns ApiKeyConfigured (a bool) but never the secret.
public class ClaudeSettingsDto
{
    public string? Model { get; set; }
    public bool ApiKeyConfigured { get; set; }
}

public class ClaudeSettingsUpdate
{
    public string? Model { get; set; }
    // Only applied when non-empty — leaving it blank keeps the existing key.
    public string? ApiKey { get; set; }
}

// Gemini API key is write-only: GET returns ApiKeyConfigured (a bool) but never the secret.
public class GeminiSettingsDto
{
    public string? Model { get; set; }
    public bool ApiKeyConfigured { get; set; }
}

public class GeminiSettingsUpdate
{
    public string? Model { get; set; }
    // Only applied when non-empty — leaving it blank keeps the existing key.
    public string? ApiKey { get; set; }
}

// Groq API key is write-only: GET returns ApiKeyConfigured (a bool) but never the secret.
public class GroqSettingsDto
{
    public string? Model { get; set; }
    public bool ApiKeyConfigured { get; set; }
}

public class GroqSettingsUpdate
{
    public string? Model { get; set; }
    // Only applied when non-empty — leaving it blank keeps the existing key.
    public string? ApiKey { get; set; }
}

// Which AI backend every feature currently calls through.
public class AiProviderSettingsDto
{
    public string Provider { get; set; } = "claude";
}

public class AiProviderSettingsUpdate
{
    public string Provider { get; set; } = "claude";
}

// SMTP password is write-only, same pattern as the Azure DevOps PAT.
public class AlertSettingsDto
{
    public bool Enabled { get; set; }
    public bool TeamsWebhookConfigured { get; set; }
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpFrom { get; set; }
    public string? SmtpTo { get; set; }
    public bool SmtpPasswordConfigured { get; set; }
}

public class AlertSettingsUpdate
{
    public bool Enabled { get; set; }
    // Only applied when non-empty — leaving it blank keeps the existing webhook URL.
    public string? TeamsWebhookUrl { get; set; }
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    // Only applied when non-empty — leaving it blank keeps the existing password.
    public string? SmtpPassword { get; set; }
    public string? SmtpFrom { get; set; }
    public string? SmtpTo { get; set; }
}

// Master switch + dry-run flag for auto-remediation. Per-(environment, module) mapping to a
// build pipeline lives in RemediationRule rows, exposed separately via /api/settings/remediation-rules.
public class AutoRemediationSettingsDto
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; }
}

public class AutoRemediationSettingsUpdate
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; }
}
