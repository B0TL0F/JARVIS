namespace Monitoring_API.Models;

public class MonitoringTarget
{
    public string Module { get; set; } = string.Empty;

    // Null/empty ApiHost means this module has no API to check (e.g. a synthetic
    // "Shell-UI root" entry that only probes the shared UI origin).
    public string? RoutePrefix { get; set; }
    public string? ApiHost { get; set; }
}

public class MonitoringEnvironment
{
    public string Name { get; set; } = string.Empty;

    public List<MonitoringTarget> Targets { get; set; } = new();
}

public class MonitoringTargetsOptions
{
    public const string SectionName = "Monitoring";

    public int PollingIntervalSeconds { get; set; } = 60;
    public int HttpTimeoutSeconds { get; set; } = 15;
    public int RetentionDays { get; set; } = 7;
    public List<MonitoringEnvironment> Environments { get; set; } = new();
}
