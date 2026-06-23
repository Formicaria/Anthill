using Anthill.Core.Common;
using Anthill.Core.Security;

namespace Anthill.Core.Memory;

/// <summary>
/// Operator-account storage: create/list/update/delete users plus the credential check used at
/// login. Usernames are stored lower-cased so logins are case-insensitive. Password hashes are
/// produced by <see cref="PasswordHasher"/> and are the only credential material persisted.
/// </summary>
public sealed partial class SqliteMemory
{
    public static string NormalizeUsername(string? username) => (username ?? "").Trim().ToLowerInvariant();

    public int CountUsers() => (int)AsLong(Scalar("SELECT COUNT(*) FROM users"));

    public int CountAdmins() =>
        (int)AsLong(Scalar("SELECT COUNT(*) FROM users WHERE role = @r AND active = 1", ("@r", UserRoles.Admin)));

    public bool UserExists(string username) =>
        Scalar("SELECT 1 FROM users WHERE username = @u", ("@u", NormalizeUsername(username))) is not null;

    /// <summary>Creates an account. Returns an error message, or empty string on success.</summary>
    public string CreateUser(string username, string password, string role)
    {
        var u = NormalizeUsername(username);
        if (u.Length < 3) return "Username must be at least 3 characters.";
        if (!System.Text.RegularExpressions.Regex.IsMatch(u, "^[a-z0-9_.-]+$"))
            return "Username may only contain letters, numbers, '.', '_' and '-'.";
        var normalizedRole = UserRoles.Normalize(role);
        if (normalizedRole.Length == 0) return "Role must be 'admin' or 'coordinator'.";
        var pwError = PasswordHasher.Validate(password);
        if (pwError.Length > 0) return pwError;
        if (UserExists(u)) return $"User '{u}' already exists.";

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                "INSERT INTO users (username, password_hash, role, active, created_at) VALUES (@u, @h, @r, 1, @c)",
                ("@u", u), ("@h", PasswordHasher.Hash(password)), ("@r", normalizedRole), ("@c", AnthillTime.NowUtc().ToIso()));
        }
        return "";
    }

    public Dictionary<string, object?>? GetUser(string username) =>
        Query("SELECT username, role, active, created_at, last_login_at FROM users WHERE username = @u",
            ("@u", NormalizeUsername(username))).FirstOrDefault();

    public List<Dictionary<string, object?>> ListUsers() =>
        Query("SELECT username, role, active, created_at, last_login_at FROM users ORDER BY role DESC, username ASC");

    /// <summary>
    /// Validates a login. Returns the account row (without the hash) on success, or null on any
    /// failure — unknown user, wrong password, or deactivated account. Stamps last_login_at.
    /// </summary>
    public Dictionary<string, object?>? VerifyLogin(string username, string password)
    {
        var u = NormalizeUsername(username);
        var row = Query("SELECT username, password_hash, role, active FROM users WHERE username = @u", ("@u", u)).FirstOrDefault();
        if (row is null) return null;
        if (AsLong(row.GetValueOrDefault("active")) != 1) return null;
        if (!PasswordHasher.Verify(password, row.GetValueOrDefault("password_hash") as string)) return null;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "UPDATE users SET last_login_at = @t WHERE username = @u",
                ("@t", AnthillTime.NowUtc().ToIso()), ("@u", u));
        }
        return new Dictionary<string, object?>
        {
            ["username"] = u, ["role"] = row.GetValueOrDefault("role"),
        };
    }

    public string SetUserPassword(string username, string newPassword)
    {
        var u = NormalizeUsername(username);
        if (!UserExists(u)) return $"User '{u}' does not exist.";
        var pwError = PasswordHasher.Validate(newPassword);
        if (pwError.Length > 0) return pwError;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "UPDATE users SET password_hash = @h WHERE username = @u",
                ("@h", PasswordHasher.Hash(newPassword)), ("@u", u));
        }
        return "";
    }

    public string SetUserRole(string username, string role)
    {
        var u = NormalizeUsername(username);
        var normalizedRole = UserRoles.Normalize(role);
        if (normalizedRole.Length == 0) return "Role must be 'admin' or 'coordinator'.";
        if (!UserExists(u)) return $"User '{u}' does not exist.";
        // Never allow demoting the last remaining admin — that would lock everyone out of admin.
        if (normalizedRole != UserRoles.Admin && IsLastAdmin(u))
            return "Cannot change role: this is the only administrator.";
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "UPDATE users SET role = @r WHERE username = @u", ("@r", normalizedRole), ("@u", u));
        }
        return "";
    }

    public string SetUserActive(string username, bool active)
    {
        var u = NormalizeUsername(username);
        if (!UserExists(u)) return $"User '{u}' does not exist.";
        if (!active && IsLastAdmin(u)) return "Cannot deactivate the only administrator.";
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "UPDATE users SET active = @a WHERE username = @u", ("@a", active ? 1 : 0), ("@u", u));
        }
        return "";
    }

    public string DeleteUser(string username)
    {
        var u = NormalizeUsername(username);
        if (!UserExists(u)) return $"User '{u}' does not exist.";
        if (IsLastAdmin(u)) return "Cannot delete the only administrator.";
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "DELETE FROM users WHERE username = @u", ("@u", u));
        }
        return "";
    }

    /// <summary>True if the named account is an active admin and the only one left.</summary>
    private bool IsLastAdmin(string username)
    {
        var u = NormalizeUsername(username);
        var row = Query("SELECT role, active FROM users WHERE username = @u", ("@u", u)).FirstOrDefault();
        if (row is null) return false;
        var isActiveAdmin = (row.GetValueOrDefault("role") as string) == UserRoles.Admin && AsLong(row.GetValueOrDefault("active")) == 1;
        return isActiveAdmin && CountAdmins() <= 1;
    }
}
