using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Models;

namespace Monitoring_API.Controllers;

// Paginated audit-log view (ported from Sentinel's getActivityLogs). Admin-only.
[ApiController]
[Route("api")]
public class ActivityController : ControllerBase
{
    private readonly MonitoringDbContext _db;

    public ActivityController(MonitoringDbContext db)
    {
        _db = db;
    }

    [HttpGet("activity")]
    public async Task<ActionResult<ActivityLogPageDto>> Get(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin role required." });

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var total = await _db.ActivityLogs.CountAsync(ct);
        var items = await _db.ActivityLogs
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new ActivityLogPageDto
        {
            Page = page,
            PageSize = pageSize,
            Total = total,
            Items = items
        });
    }
}
