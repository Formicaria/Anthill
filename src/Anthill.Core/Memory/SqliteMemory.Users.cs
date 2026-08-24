using Anthill.Core.Common;
using Anthill.Core.Security;

namespace Anthill.Core.Memory;

/// <summary>What happened when the first administrator was requested. Typed, never inferred.</summary>
public enum InitialAdminOutcome
{
    Created,
    /// <summary>An administrator already existed when the transaction ran. Not an error the caller
    /// caused — it is the race being won by somebody else, and the caller must be told which.</summary>
    AlreadyInitialised,
    /// <summary>The username or password did not pass validation.</summary>
    Rejected,
}

/// <param name="Error">Operator-facing text. Empty when <see cref="InitialAdminOutcome.Created"/>.
/// It is a MESSAGE, never the thing a caller branches on — see the typed outcome beside it.</param>
public sealed record InitialAdminResult(InitialAdminOutcome Outcome, string Error);

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

    /// <summary>
    /// Create the FIRST administrator, or refuse because one already exists. One transaction.
    /// v0.3.8.91.
    ///
    /// WHY THIS IS NOT `CreateUser`. `/auth/setup` used to ask `CountUsers() > 0` and then, as a
    /// separate operation, call `CreateUser`. Two concurrent setup requests with different usernames
    /// both saw zero users and both inserted an administrator — the read-then-write gap, on the one
    /// account that has no prior authority to check. `CreateUser`'s own `_writeLock` does not help:
    /// it wraps the INSERT, the zero-user question was asked outside it, and it is an instance lock
    /// with no meaning across processes sharing a colony database.
    ///
    /// The count and the insert are now ONE transaction, which is the same shape `TryClaimTask`
    /// already uses and for the same stated reason: a precondition checked outside the transaction
    /// is not a precondition.
    ///
    /// Validation stays outside the transaction deliberately — a bad password is not a race, and
    /// holding a write transaction open across hashing (which is intentionally slow) would serialise
    /// every writer behind it.
    /// </summary>
    public InitialAdminResult CreateInitialAdministrator(string username, string password)
    {
        var u = NormalizeUsername(username);
        if (u.Length < 3) return new(InitialAdminOutcome.Rejected, "Username must be at least 3 characters.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(u, "^[a-z0-9_.-]+$"))
            return new(InitialAdminOutcome.Rejected, "Username may only contain letters, numbers, '.', '_' and '-'.");
        var pwError = PasswordHasher.Validate(password);
        if (pwError.Length > 0) return new(InitialAdminOutcome.Rejected, pwError);

        var hash = PasswordHasher.Hash(password);

        lock (_writeLock)
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction();

            using (var guard = conn.CreateCommand())
            {
                guard.Transaction = tx;
                guard.CommandText = "SELECT COUNT(*) FROM users";
                if (Convert.ToInt64(guard.ExecuteScalar() ?? 0L) > 0)
                    return new(InitialAdminOutcome.AlreadyInitialised,
                        "Setup already complete. An administrator already exists.");
            }

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText =
                    "INSERT INTO users (username, password_hash, role, active, created_at) " +
                    "VALUES (@u, @h, @r, 1, @c)";
                insert.Parameters.AddWithValue("@u", u);
                insert.Parameters.AddWithValue("@h", hash);
                insert.Parameters.AddWithValue("@r", UserRoles.Admin);
                insert.Parameters.AddWithValue("@c", AnthillTime.NowUtc().ToIso());
                insert.ExecuteNonQuery();
            }

            tx.Commit();
            return new(InitialAdminOutcome.Created, "");
        }
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
