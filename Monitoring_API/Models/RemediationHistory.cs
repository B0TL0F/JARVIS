namespace Monitoring_API.Models;

// Tracks auto-remediation actions so AlertNotificationService can rate-limit/cooldown and audit
// them — structurally similar to AlertHistory but a separate table: this models "which action was
// taken and what happened", not "has this notification condition been sent recently", and needs a
// rolling-hour rate counter rather than a resend-after-N-hours reminder.
public class RemediationHistory
{
    public int Id { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    // Stable per-ongoing-condition key: "{Environment}:{Module}:remediation" (no timestamp).
    public string DedupKey { get; set; } = string.Empty;
    public int RuleId { get; set; }
    // "triggered" | "would_trigger" (dry run) | "skipped_rate_limit" | "skipped_cooldown"
    public string Action { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public int? BuildId { get; set; }
    public string? Error { get; set; }
    public DateTime FiredAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}
