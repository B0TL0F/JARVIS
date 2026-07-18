using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Middleware;
using Monitoring_API.Models;
using Monitoring_API.Services;

namespace Monitoring_API.Controllers;

// User management + RBAC (ported from Sentinel's getUsers/addUser/deleteUser and
// role handling). Every request here is already authenticated by BasicAuthMiddleware;
// mutating actions additionally require the "admin" role.
[ApiController]
[Route("api")]
public class UsersController : ControllerBase
{
    private readonly MonitoringDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly ActivityLogger _activity;

    public UsersController(MonitoringDbContext db, IPasswordHasher<AppUser> hasher, ActivityLogger activity)
    {
        _db = db;
        _hasher = hasher;
        _activity = activity;
    }

    // Current user's identity + role — the Angular app calls this right after login
    // to gate admin-only views. Logging here doubles as the "login" audit event,
    // since the UI hits /api/me exactly once when signing in.
    [HttpGet("me")]
    public async Task<ActionResult<object>> Me()
    {
        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "login", "Signed in", BasicAuthMiddleware.ClientIp(HttpContext));

        return Ok(new
        {
            id = CurrentUser.Id(User),
            username = CurrentUser.Name(User),
            role = CurrentUser.Role(User)
        });
    }

    [HttpGet("users")]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin role required." });

        var users = await _db.AppUsers
            .OrderBy(u => u.Username)
            .Select(u => new UserDto { Id = u.Id, Username = u.Username, Role = u.Role, CreatedAtUtc = u.CreatedAtUtc })
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPost("users")]
    public async Task<ActionResult<UserDto>> AddUser([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin role required." });

        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        {
            return BadRequest(new { error = "Username and password are required." });
        }
        if (!Roles.IsValid(req.Role))
        {
            return BadRequest(new { error = "Role must be 'admin' or 'developer'." });
        }
        if (await _db.AppUsers.AnyAsync(u => u.Username == req.Username, ct))
        {
            return Conflict(new { error = "A user with that username already exists." });
        }

        var user = new AppUser
        {
            Username = req.Username,
            Role = req.Role,
            CreatedAtUtc = DateTime.UtcNow
        };
        user.PasswordHash = _hasher.HashPassword(user, req.Password);

        _db.AppUsers.Add(user);
        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "user.create", $"Created user '{user.Username}' ({user.Role})", BasicAuthMiddleware.ClientIp(HttpContext));

        return CreatedAtAction(nameof(GetUsers), new UserDto
        {
            Id = user.Id, Username = user.Username, Role = user.Role, CreatedAtUtc = user.CreatedAtUtc
        });
    }

    [HttpDelete("users/{id:int}")]
    public async Task<IActionResult> DeleteUser(int id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin role required." });

        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        // Guard: never delete yourself, and never remove the last admin (would lock
        // everyone out of user management).
        if (id == CurrentUser.Id(User))
        {
            return BadRequest(new { error = "You cannot delete your own account." });
        }
        if (user.Role == Roles.Admin && await _db.AppUsers.CountAsync(u => u.Role == Roles.Admin, ct) <= 1)
        {
            return BadRequest(new { error = "Cannot delete the last admin account." });
        }

        _db.AppUsers.Remove(user);
        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "user.delete", $"Deleted user '{user.Username}'", BasicAuthMiddleware.ClientIp(HttpContext));

        return NoContent();
    }

    // Changes an existing user's role. Admin-only. Same last-admin protection as delete —
    // demoting the last remaining admin would lock everyone out of user management.
    [HttpPut("users/{id:int}/role")]
    public async Task<ActionResult<UserDto>> ChangeRole(int id, [FromBody] ChangeUserRoleRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin role required." });

        if (!Roles.IsValid(req.Role))
        {
            return BadRequest(new { error = "Role must be 'admin' or 'developer'." });
        }

        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        if (user.Role == Roles.Admin && req.Role != Roles.Admin
            && await _db.AppUsers.CountAsync(u => u.Role == Roles.Admin, ct) <= 1)
        {
            return BadRequest(new { error = "Cannot demote the last admin account." });
        }

        var oldRole = user.Role;
        user.Role = req.Role;
        await _db.SaveChangesAsync(ct);

        await _activity.LogAsync(CurrentUser.Id(User), CurrentUser.Name(User),
            "user.role-change", $"Changed '{user.Username}' role from {oldRole} to {user.Role}", BasicAuthMiddleware.ClientIp(HttpContext));

        return Ok(new UserDto { Id = user.Id, Username = user.Username, Role = user.Role, CreatedAtUtc = user.CreatedAtUtc });
    }
}
