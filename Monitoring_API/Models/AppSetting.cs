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
