namespace Monitoring_API.Models;

// Audit trail ported from Sentinel's activity_log table. One row per meaningful
// action (login, user create/delete, etc.) so admins can see who did what, when.
public class ActivityLog
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    public string Username { get; set; } = "system";
    public string Action { get; set; } = string.Empty;   // e.g. "login", "user.create"
    public string Details { get; set; } = string.Empty;  // human-readable detail
    public string? Ip { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class ActivityLogPageDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
    public List<ActivityLog> Items { get; set; } = new();
}
