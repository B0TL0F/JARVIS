using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Models;
using Monitoring_API.Services;

namespace Monitoring_API.Middleware;

// Basic-auth gate, reworked for the Sentinel-ported RBAC. Credentials are now
// validated against the AppUsers table (per-user, hashed) instead of a single
// shared config credential. On success it attaches a ClaimsPrincipal carrying the
// user's id/name/role so controllers can enforce admin-only actions and the
// activity logger can attribute actions. The config credential is used only to
// seed the first admin (see DbSeeder), never checked here directly.
public class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;

    public BasicAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // CORS preflight carries no Authorization header — let it through (CORS
        // middleware runs before this and normally short-circuits, this is belt-and-braces).
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var db = context.RequestServices.GetRequiredService<MonitoringDbContext>();
        var hasher = context.RequestServices.GetRequiredService<IPasswordHasher<AppUser>>();

        string? authHeader = context.Request.Headers.Authorization;
        if (authHeader is not null && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = authHeader["Basic ".Length..].Trim();
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var separatorIndex = decoded.IndexOf(':');
                if (separatorIndex > 0)
                {
                    var username = decoded[..separatorIndex];
                    var password = decoded[(separatorIndex + 1)..];

                    var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
                    if (user is not null)
                    {
                        var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
                        if (verify is PasswordVerificationResult.Success
                            or PasswordVerificationResult.SuccessRehashNeeded)
                        {
                            AttachPrincipal(context, user);
                            await _next(context);
                            return;
                        }
                    }

                    // Credentials supplied but wrong — audit the failed attempt.
                    var logger = context.RequestServices.GetRequiredService<ActivityLogger>();
                    await logger.LogAsync(null, username, "login.failed",
                        "Invalid credentials", ClientIp(context));
                }
            }
            catch (FormatException)
            {
                // malformed base64 — fall through to 401
            }
        }

        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Jarvis\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private static void AttachPrincipal(HttpContext context, AppUser user)
    {
        var identity = new ClaimsIdentity("Basic");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Username));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
        context.User = new ClaimsPrincipal(identity);
    }

    public static string? ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();
}
