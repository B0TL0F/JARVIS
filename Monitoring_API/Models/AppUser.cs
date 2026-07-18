namespace Monitoring_API.Models;

// Roles for the Sentinel-ported RBAC. Kept as plain strings (not an enum) so the
// value stored in Postgres is human-readable and matches what the UI sends.
public static class Roles
{
    public const string Admin = "admin";
    public const string Developer = "developer";

    public static bool IsValid(string? role) =>
        role == Admin || role == Developer;
}

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;

    // PBKDF2 hash produced by Microsoft.AspNetCore.Identity.PasswordHasher — never a
    // plaintext password. The single shared basic-auth credential is only used to seed
    // the first admin (see DbSeeder); every login after that validates against this.
    public string PasswordHash { get; set; } = string.Empty;

    public string Role { get; set; } = Roles.Developer;
    public DateTime CreatedAtUtc { get; set; }
}

public class UserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = Roles.Developer;
}

public class ChangeUserRoleRequest
{
    public string Role { get; set; } = Roles.Developer;
}
