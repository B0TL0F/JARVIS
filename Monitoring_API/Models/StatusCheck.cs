namespace Monitoring_API.Models;

public enum CheckType
{
    Api,
    Db
}

public class StatusCheck
{
    public long Id { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public CheckType CheckType { get; set; }
    public DateTime TimestampUtc { get; set; }
    public bool IsUp { get; set; }
    public int? ResponseTimeMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ServiceStatusDto
{
    public string Module { get; set; } = string.Empty;
    // Null means this module has no API/DB to check (e.g. the Shell-UI-only entry) —
    // distinct from an actual failed check, which is a populated DTO with IsUp = false.
    public CheckResultDto? Api { get; set; }
    public CheckResultDto? Db { get; set; }
    public double UptimePercent24h { get; set; }
}

public class CheckResultDto
{
    public bool IsUp { get; set; }
    public int? ResponseTimeMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? TimestampUtc { get; set; }
}

public class EnvironmentStatusDto
{
    public string Environment { get; set; } = string.Empty;
    public List<ServiceStatusDto> Modules { get; set; } = new();
}
