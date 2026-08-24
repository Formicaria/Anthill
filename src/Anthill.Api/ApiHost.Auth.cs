using System.Reflection;
using Anthill.Core.Agents;
using Anthill.Core.Shadow;
using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Conversations;   // v3.7.0: conversations, escalation policy and run state
using Anthill.Core.Diagnostics;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Orchestration;
using Anthill.Core.Planning;
using Anthill.Core.Readiness;
using Anthill.Core.Sandbox;   // LoopBudget — the agent loop's bounds
using Anthill.Core.Security;
using Anthill.Core.Tools;      // ToolInventory, ToolAuthorization — the /tools report
// `Task` here is Anthill.Core.Domain.Task (the mission task). The threading one must be named.
using ThreadingTask = System.Threading.Tasks.Task;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;


namespace Anthill.Api;

/// <summary>
/// Authentication, identity and permission gating — every route guard in one place.
///
/// v3.8.17 — split out of ApiHost.cs, which was 3,294 lines and 102 endpoints. Same class,
/// same behaviour: ApiHost has been `public static partial` with eight files since the homelab
/// moved, so this is where the file was always going to divide.
/// </summary>
public static partial class ApiHost
{
    // ---- Authentication + operator accounts ----
    private static void MapAuthEndpoints(WebApplication app)
    {
        // Public: tells the UI whether to show first-run setup or the login screen.
        app.MapGet("/auth/status", () => ApiJson.Ok(new Dictionary<string, object?>
        {
            ["setup_required"] = Queen.Memory.CountUsers() == 0,
            // v0.3.8.91: whether the first-run token is needed, so the console can ask for it. Says
            // only WHETHER, never the value — this endpoint is public by necessity.
            ["setup_token_required"] = SetupAuthority.SecretRequired,
            ["auth_enabled"] = AnthillRuntime.EnableApiAuth,
            ["user_count"] = Queen.Memory.CountUsers(),
        }));

        // First-run only: create the initial administrator and log them straight in.
        //
        // NOT PUBLIC ANY MORE, on any deployment that can be reached from off-box. v0.3.8.91.
        //
        // This endpoint used to be gated on `CountUsers() == 0` and nothing else, while every safety
        // profile forces the listener onto 0.0.0.0. On a server or LXC that made the administrator
        // account a race won by whoever reached the port first — and `operator_shell_enabled` shipped
        // true, so winning it meant a shell on the host. `SetupAuthority` mints a single-use secret
        // at startup for exactly the window where no account exists yet to be the boundary; see that
        // class for why the rule is written on the BIND and not on the caller's address.
        //
        // The zero-user check now lives INSIDE the insert transaction
        // (`CreateInitialAdministrator`). Asking it here as well would be the read-then-write gap
        // this release closed, reintroduced one line above the fix.
        app.MapPost("/auth/setup", async (HttpContext ctx) =>
        {
            if (!AuthLimiter_TryConsume(ctx)) return ApiJson.Error("Too many attempts. Try again later.", "rate_limited");
            SetupRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<SetupRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var presented = body?.SetupToken ?? ctx.Request.Headers["X-Anthill-Setup-Token"].FirstOrDefault();
            switch (SetupAuthority.Admit(presented))
            {
                case SetupAdmission.SecretRequired:
                    return ApiJson.Error(
                        "This installation is reachable from the network, so first-run setup needs "
                      + "the token this process printed at startup. It is in the service log and in "
                      + $"{SetupAuthority.SecretFileName} under the workspace directory. Send it as "
                      + "the 'setup_token' field.", "forbidden");
                case SetupAdmission.SecretWrong:
                    return ApiJson.Error("Setup token is not correct.", "forbidden");
                case SetupAdmission.AlreadyComplete:
                    return ApiJson.Error("Setup already complete. An administrator already exists.", "bad_request");
            }

            var username = string.IsNullOrWhiteSpace(body?.Username) ? "admin" : body!.Username!.Trim();
            var created = Queen.Memory.CreateInitialAdministrator(username, body?.Password ?? "");
            if (created.Outcome != InitialAdminOutcome.Created)
                return ApiJson.Error(created.Error, "bad_request");

            // AFTER the row is committed, never before: a secret spent on a failed insert would
            // leave a deployment with no administrator and no way to make one.
            SetupAuthority.Consume();

            var token = AuthSessions.Issue(SqliteMemory.NormalizeUsername(username), UserRoles.Admin);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["token"] = token, ["username"] = SqliteMemory.NormalizeUsername(username), ["role"] = UserRoles.Admin,
            }, "Administrator created. You are now signed in.");
        });

        // Public, rate-limited: username + password -> session token.
        app.MapPost("/auth/login", async (HttpContext ctx) =>
        {
            var ip = ClientIp(ctx);
            if (AuthLimiter.IsLimited(ip)) return ApiJson.Error("Too many failed logins. Try again later.", "rate_limited");
            LoginRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<LoginRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var ok = Queen.Memory.VerifyLogin(body?.Username ?? "", body?.Password ?? "");
            if (ok is null)
            {
                AuthLimiter.RecordAttempt(ip);
                return ApiJson.Error("Invalid username or password.", "unauthorized");
            }
            AuthLimiter.Clear(ip);
            var role = ok.GetValueOrDefault("role") as string ?? UserRoles.Coordinator;
            var username = ok.GetValueOrDefault("username") as string ?? "";
            var token = AuthSessions.Issue(username, role);
            return ApiJson.Ok(new Dictionary<string, object?> { ["token"] = token, ["username"] = username, ["role"] = role }, "Signed in.");
        });

        app.MapPost("/auth/logout", (HttpContext ctx) =>
        {
            AuthSessions.Revoke(ExtractToken(ctx));
            return ApiJson.Ok(new Dictionary<string, object?> { ["ok"] = true }, "Signed out.");
        });

        app.MapGet("/auth/me", (HttpContext ctx) =>
        {
            var id = ResolveIdentity(ctx);
            return id is null
                ? ApiJson.Error("Unauthorized.", "unauthorized")
                : ApiJson.Ok(new Dictionary<string, object?> { ["username"] = id.Username, ["role"] = id.Role });
        });

        // ---- User management (admin-only via the role layer) ----
        app.MapGet("/users", (HttpContext ctx) =>
            RequireAuth(ctx, "manage_users") ?? ApiJson.Ok(Queen.Memory.ListUsers().Select(UserDict).ToList()));

        app.MapPost("/users", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_users"); if (auth is not null) return auth;
            UserRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<UserRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var err = Queen.Memory.CreateUser(body?.Username ?? "", body?.Password ?? "", body?.Role ?? UserRoles.Coordinator);
            if (err.Length > 0) return ApiJson.Error(err, "bad_request");
            return ApiJson.Ok(UserDict(Queen.Memory.GetUser(body!.Username!)!), "User created.");
        });

        app.MapPatch("/users/{username}", async (HttpContext ctx, string username) =>
        {
            var auth = RequireAuth(ctx, "manage_users"); if (auth is not null) return auth;
            if (Queen.Memory.GetUser(username) is null) return ApiJson.Error($"No user found: {username}", "not_found");
            UserPatch? body;
            try { body = await ctx.Request.ReadFromJsonAsync<UserPatch>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var norm = SqliteMemory.NormalizeUsername(username);
            if (!string.IsNullOrEmpty(body?.Password))
            {
                var e = Queen.Memory.SetUserPassword(norm, body.Password); if (e.Length > 0) return ApiJson.Error(e, "bad_request");
                AuthSessions.RevokeUser(norm); // force re-login with the new password
            }
            if (!string.IsNullOrEmpty(body?.Role))
            {
                var e = Queen.Memory.SetUserRole(norm, body.Role); if (e.Length > 0) return ApiJson.Error(e, "bad_request");
                AuthSessions.RevokeUser(norm); // new permissions take effect on next login
            }
            if (body?.Active is bool active)
            {
                var e = Queen.Memory.SetUserActive(norm, active); if (e.Length > 0) return ApiJson.Error(e, "bad_request");
                if (!active) AuthSessions.RevokeUser(norm);
            }
            return ApiJson.Ok(UserDict(Queen.Memory.GetUser(norm)!), "User updated.");
        });

        app.MapDelete("/users/{username}", (HttpContext ctx, string username) =>
        {
            var auth = RequireAuth(ctx, "manage_users"); if (auth is not null) return auth;
            var norm = SqliteMemory.NormalizeUsername(username);
            var me = ResolveIdentity(ctx);
            if (me is not null && string.Equals(me.Username, norm, StringComparison.OrdinalIgnoreCase))
                return ApiJson.Error("You cannot delete your own account while signed in.", "bad_request");
            var err = Queen.Memory.DeleteUser(norm);
            if (err.Length > 0) return ApiJson.Error(err, "bad_request");
            AuthSessions.RevokeUser(norm);
            return ApiJson.Ok(new Dictionary<string, object?> { ["username"] = norm }, "User removed.");
        });
    }

    private static Dictionary<string, object?> UserDict(Dictionary<string, object?> row) => new()
    {
        ["username"] = row.GetValueOrDefault("username"),
        ["role"] = row.GetValueOrDefault("role"),
        ["active"] = Convert.ToInt64(row.GetValueOrDefault("active") ?? 0L) == 1,
        ["created_at"] = row.GetValueOrDefault("created_at"),
        ["last_login_at"] = row.GetValueOrDefault("last_login_at"),
    };

    /// <summary>Consumes one auth-limiter slot for an unauthenticated, abuse-prone endpoint.</summary>
    private static bool AuthLimiter_TryConsume(HttpContext ctx)
    {
        var ip = ClientIp(ctx);
        if (AuthLimiter.IsLimited(ip)) return false;
        AuthLimiter.RecordAttempt(ip);
        return true;
    }

    /// <summary>
    /// Assembles the structured mission report for /missions/{id}/report: mission-level outcome
    /// and final output, per-task readable results (coder JSON translated to plain English),
    /// tangible changes (patch proposals + approval state), and problems (failures, timeouts,
    /// unparseable proposals) — everything the console needs to show what actually happened.
    /// </summary>
    /// <summary>True when the authenticated caller's role permits the named permission (and it's enabled).</summary>
    private static bool CallerHas(HttpContext ctx, string permission)
    {
        if (!AnthillRuntime.EnableApiAuth) return true;
        var identity = ResolveIdentity(ctx);
        return identity is not null && UserRoles.RoleAllows(identity.Role, permission) && ApiPermissionAllowed(permission);
    }

    private static void ProtectedJson(WebApplication app, string path, string permission, Func<HttpContext, IResult> handler) =>
        app.MapGet(path, (HttpContext ctx) => RequireAuth(ctx, permission) ?? handler(ctx));

    private static void ProtectedText(WebApplication app, string path, string permission, Func<string> handler) =>
        app.MapGet(path, (HttpContext ctx) => RequireAuth(ctx, permission) ?? Results.Text(handler(), "text/plain"));

    private static IResult? RequireAuth(HttpContext ctx, string permission)
    {
        var ip = ClientIp(ctx);
        if (AnthillRuntime.EnableApiAuth)
        {
            if (AuthLimiter.IsLimited(ip))
                return ApiJson.Error("Too many failed authentication attempts. Try again later.", "rate_limited");
            var identity = ResolveIdentity(ctx);
            if (identity is null)
            {
                AuthLimiter.RecordAttempt(ip);
                return ApiJson.Error("Unauthorized. Log in to the colony.", "unauthorized");
            }
            AuthLimiter.Clear(ip); // a valid session must not consume the failed-auth budget
            if (!UserRoles.RoleAllows(identity.Role, permission))
                return ApiJson.Error($"Permission denied: your role ({identity.Role}) is not allowed to {permission}.", "permission_denied");
        }
        // Capability gate: the feature must also be enabled at all (independent of who you are).
        if (!ApiPermissionAllowed(permission))
            return ApiJson.Error($"Permission denied: {permission} is disabled.", "permission_denied");
        return null;
    }

    private static bool ApiPermissionAllowed(string permission) => AnthillRuntime.ApiPermissions.GetValueOrDefault(permission, false);

    /// <summary>
    /// Resolves the caller's identity from their bearer token: first as a login session, then —
    /// for back-compat with scripts/CI — as the optional static ANTHILL_API_TOKEN, which acts as a
    /// programmatic admin when configured. Returns null when neither matches.
    /// </summary>
    private static AuthSession? ResolveIdentity(HttpContext ctx)
    {
        var token = ExtractToken(ctx);
        if (token is null) return null;
        var session = AuthSessions.Resolve(token);
        if (session is not null) return session;
        if (HasStaticToken() && TokenSecurity.ConstantTimeEquals(token, AnthillRuntime.ApiAuthToken))
            return new AuthSession("api-token", UserRoles.Admin, DateTime.UtcNow.AddHours(1));
        return null;
    }

    /// <summary>Acting operator's username for audit trails (v1.8.24 Patch Center actions); null when unauthenticated.</summary>
    private static string? CurrentUsername(HttpContext ctx) => ResolveIdentity(ctx)?.Username;

    /// <summary>True when a strong, non-placeholder static API token is configured for programmatic use.</summary>
    private static bool HasStaticToken() =>
        !string.IsNullOrEmpty(AnthillRuntime.ApiAuthToken)
        && AnthillRuntime.ApiAuthToken != AnthillRuntime.ApiTokenDefaultPlaceholder
        && AnthillRuntime.ApiAuthToken.Length >= AnthillRuntime.ApiTokenMinLength;

    private static string? ExtractToken(HttpContext ctx)
    {
        var direct = ctx.Request.Headers["X-Anthill-Token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(direct)) return direct;
        var authz = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authz) && authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authz["Bearer ".Length..].Trim();
        return null;
    }

    private static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
