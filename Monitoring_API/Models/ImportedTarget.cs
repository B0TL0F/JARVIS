namespace Monitoring_API.Models;

// A monitored target stored in the DB. Origins:
//  - "ocelot": discovered by the Ocelot importer from a gateway config file.
//  - "manual": created by an admin through the Targets management UI for a
//    module that isn't in the static config at all.
//  - "override": an admin edited a target that was originally defined only in
//    config/targets.json — this row's ApiHost/RoutePrefix now takes
//    precedence over the static config for that (Environment, Module), see
//    TargetProvider. Deleting an override row reverts to the config default.
// All three are fully editable/deletable via the CRUD API.
//
// An Ocelot route like host "acs2devexport.azurewebsites.net" +
// path "/api_export/verifyapi/check" becomes
// ApiHost="acs2devexport.azurewebsites.net", RoutePrefix="api_export".
public class ImportedTarget
{
    public int Id { get; set; }
    public string Environment { get; set; } = string.Empty;   // group name e.g. DEV/UAT/PROD
    public string Module { get; set; } = string.Empty;        // e.g. "EXPORT"
    public string ApiHost { get; set; } = string.Empty;
    public string RoutePrefix { get; set; } = string.Empty;

    public string Origin { get; set; } = "ocelot";           // "ocelot" | "manual" | "override"
    public string SourceFile { get; set; } = string.Empty;    // originating ocelot filename (if any)
    public DateTime CreatedAtUtc { get; set; }
}

public class TargetDto
{
    // Null for a target that's only defined in the static config/targets.json
    // and has never been edited — there's no DB row yet. Once edited, an
    // "override" row is created and this becomes non-null.
    public int? Id { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string ApiHost { get; set; } = string.Empty;
    public string RoutePrefix { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;         // "config" | "manual" | "ocelot" | "override"
    public string? HealthCheckUrl { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
}

// Create/update payload from the Targets management UI.
public class TargetUpsertRequest
{
    public string Environment { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string? ApiHost { get; set; }
    public string? RoutePrefix { get; set; }
}
