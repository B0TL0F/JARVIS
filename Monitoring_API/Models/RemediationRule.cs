namespace Monitoring_API.Models;

// Explicit (Environment, Module) -> Azure DevOps build pipeline mapping for auto-remediation.
// Deliberately DB-backed and admin-edited (like AlertHistory/AppSetting), not a targets.json-style
// file — there's no external source of truth to mirror here, this is pure Jarvis policy.
// Never fuzzy-matched: a module with no rule (or a disabled rule) is simply never auto-remediated.
public class RemediationRule
{
    public int Id { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public int AzureDevOpsDefinitionId { get; set; }
    public string? Branch { get; set; }
    public bool Enabled { get; set; }
    public int MaxActionsPerHour { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public class RemediationRuleDto
{
    public int Id { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public int AzureDevOpsDefinitionId { get; set; }
    public string? Branch { get; set; }
    public bool Enabled { get; set; }
    public int MaxActionsPerHour { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public class RemediationRuleUpsertDto
{
    public string Environment { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public int AzureDevOpsDefinitionId { get; set; }
    public string? Branch { get; set; }
    public bool Enabled { get; set; }
    public int MaxActionsPerHour { get; set; } = 1;
}
