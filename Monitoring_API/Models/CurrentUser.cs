using System.Security.Claims;

namespace Monitoring_API.Models;

// Convenience accessors for the ClaimsPrincipal that BasicAuthMiddleware attaches.
public static class CurrentUser
{
    public static int? Id(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static string Name(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Name) ?? "unknown";

    public static string? Role(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Role);

    public static bool IsAdmin(ClaimsPrincipal user) =>
        Role(user) == Roles.Admin;
}
