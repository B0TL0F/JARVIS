namespace Monitoring_API.Models;

// Tracks AI-triggered alert deliveries so AlertNotificationService can dedup: an ongoing
// condition (same DedupKey) notifies once, not once per polling/evaluation cycle. A new
// row opens when a condition (re-)starts; ResolvedAtUtc is set when it clears, so the
// next occurrence opens a fresh row and notifies again.
public class AlertHistory
{
    public int Id { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string AlertType { get; set; } = string.Empty; // "incident" | "flaky" | "anomaly" | "build_failure"
    // Stable per-ongoing-condition key: "{Environment}:{Module}:{AlertType}" (no timestamp).
    public string DedupKey { get; set; } = string.Empty;
    public DateTime FirstFiredAtUtc { get; set; }
    public DateTime LastFiredAtUtc { get; set; }
    public int FireCount { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? Summary { get; set; }
    public bool TeamsSent { get; set; }
    public bool EmailSent { get; set; }
}
