using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitoring_API.Data;
using Monitoring_API.Models;
using Monitoring_API.Services;

namespace Monitoring_API.Controllers;

[ApiController]
[Route("api")]
public class StatusController : ControllerBase
{
    private readonly MonitoringTargetsOptions _options;
    private readonly ITargetProvider _targetProvider;
    private readonly StatusSnapshotService _snapshot;

    public StatusController(IOptions<MonitoringTargetsOptions> options, ITargetProvider targetProvider, StatusSnapshotService snapshot)
    {
        _options = options.Value;
        _targetProvider = targetProvider;
        _snapshot = snapshot;
    }

    [HttpGet("environments")]
    public async Task<ActionResult<object>> GetEnvironments(CancellationToken ct)
    {
        var environments = await _targetProvider.GetEnvironmentsAsync(ct);
        return Ok(new
        {
            environments = environments.Select(e => e.Name),
            pollingIntervalSeconds = _options.PollingIntervalSeconds
        });
    }

    [HttpGet("status")]
    public async Task<ActionResult<object>> Get([FromQuery] string? environment, CancellationToken ct)
    {
        var environments = await _targetProvider.GetEnvironmentsAsync(ct);
        var env = environments.FirstOrDefault(e =>
            string.Equals(e.Name, environment, StringComparison.OrdinalIgnoreCase))
            ?? environments.FirstOrDefault();

        if (env is null)
        {
            return NotFound(new { error = "No environments configured." });
        }

        var modules = await _snapshot.GetModuleStatusesAsync(env, ct);

        return Ok(new
        {
            environment = env.Name,
            pollingIntervalSeconds = _options.PollingIntervalSeconds,
            generatedAtUtc = DateTime.UtcNow,
            modules
        });
    }
}
