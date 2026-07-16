using Microsoft.AspNetCore.Mvc;
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

    public SettingsController(SettingsService settings, OcelotImportService ocelot, ActivityLogger activity)
    {
        _settings = settings;
        _ocelot = ocelot;
        _activity = activity;
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
            }
        });
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
