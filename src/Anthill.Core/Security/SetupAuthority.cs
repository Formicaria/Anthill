using System.Security.Cryptography;

namespace Anthill.Core.Security;

/// <summary>Why a setup attempt was refused, or that it may proceed. Typed, never prose.</summary>
public enum SetupAdmission
{
    /// <summary>No secret is required on this deployment, or the one presented is correct.</summary>
    Admitted,
    /// <summary>A secret is required and none was presented.</summary>
    SecretRequired,
    /// <summary>A secret was presented and it is not the one this process minted.</summary>
    SecretWrong,
    /// <summary>Setup already happened. The secret is spent and cannot be replayed.</summary>
    AlreadyComplete,
}

/// <summary>
/// Who is allowed to create the first administrator. v0.3.8.91.
///
/// THE HOLE THIS CLOSES. A fresh install binds `0.0.0.0:8713` — every shipped safety profile forces
/// it — and `/auth/setup` was deliberately unauthenticated while `CountUsers() == 0`, because
/// somebody has to be able to make the first account. On a server or LXC that meant the first person
/// to reach the port chose the administrator password. `operator_shell_enabled` also shipped `true`,
/// and that feature's own comment correctly calls it host command execution for administrators. The
/// chain was: reach the port, win the race, become admin, open the terminal.
///
/// `DEPLOYMENT.md` argued the real boundary was "the operator login, not network isolation". That is
/// true from the second account onward and false for exactly the window this class governs: before
/// the first login exists there is no login to be the boundary.
///
/// WHAT IT DOES. On a deployment that can be reached from off-box, the process mints a single-use
/// secret at startup, writes it where only a local operator can read it, and `/auth/setup` requires
/// it. Setup consumes it permanently. This is a *bootstrap* credential — it authenticates the person
/// standing at the machine, which is the only identity that exists before there are accounts.
///
/// WHEN IT IS REQUIRED, and why the rule is about the BIND rather than the caller's address:
///   - bound to loopback → not required. Reaching the port already proves local access, and the
///     Windows desktop app (which forces loopback) must not send its user hunting for a token file.
///   - bound to anything else → required, for everyone, including a request from localhost.
/// The caller's remote address is deliberately NOT the test. Behind a reverse proxy every request
/// arrives from loopback, so a rule written on the caller's address would authorise the whole
/// internet through one hop. The bind cannot be spoofed by a request.
///
/// THE CASE THIS DOES NOT COVER, stated rather than implied: a reverse proxy in front of a LOOPBACK
/// bind. The proxy is then the network boundary and this process cannot see past it. An operator in
/// that shape sets `setup_token_required: true` (or `ANTHILL_REQUIRE_SETUP_TOKEN=1`) and gets the
/// secret regardless of the bind. Documented in DEPLOYMENT.md next to the proxy instructions.
/// </summary>
public static class SetupAuthority
{
    /// <summary>The file a local operator reads the secret out of, under the workspace root.</summary>
    public const string SecretFileName = "SETUP-TOKEN.txt";

    private static readonly object Gate = new();
    private static string _secret = "";
    private static bool _consumed;
    private static string _secretPath = "";

    /// <summary>Whether this process minted a secret — i.e. whether setup needs one.</summary>
    public static bool SecretRequired
    {
        get { lock (Gate) return _secret.Length > 0 && !_consumed; }
    }

    /// <summary>Where the secret was written, for the startup banner. Empty when none was minted.</summary>
    public static string SecretPath
    {
        get { lock (Gate) return _secretPath; }
    }

    /// <summary>
    /// Decide whether this deployment needs a bootstrap secret, and mint one if so.
    ///
    /// Called once at startup AFTER the config is projected and BEFORE the listener opens. Returns
    /// the secret when it minted one so the caller can print it; empty when none is needed.
    ///
    /// <paramref name="usersExist"/> is passed in rather than read here: a colony that already has
    /// an administrator has nothing to bootstrap, and minting a secret for it would leave a
    /// credential file on disk that opens nothing.
    /// </summary>
    public static string Arm(string bindHost, bool forceRequired, bool usersExist, string workspaceRoot)
    {
        lock (Gate)
        {
            _secret = "";
            _consumed = false;
            _secretPath = "";

            if (usersExist) return "";
            if (!forceRequired && UrlSafety.IsLoopbackBindHost(bindHost)) return "";

            // 160 bits, url-safe. Long enough that the rate limiter is not the thing standing
            // between an attacker and the administrator account.
            _secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            try
            {
                Directory.CreateDirectory(workspaceRoot);
                _secretPath = Path.Combine(workspaceRoot, SecretFileName);
                File.WriteAllText(_secretPath,
                    _secret + Environment.NewLine
                    + "# Anthill first-run setup token. Present it as the setup_token field on "
                    + "POST /auth/setup." + Environment.NewLine
                    + "# It is single-use and this file is deleted once an administrator exists."
                    + Environment.NewLine);
                HardenFilePermissions(_secretPath);
            }
            catch (Exception error)
            {
                // The console line is the fallback channel and is always printed by the caller, so a
                // read-only or unusual filesystem degrades to "read it from the service log" rather
                // than to "setup is impossible". It must never degrade to "no secret required".
                _secretPath = "";
                Console.Error.WriteLine($"Could not write the setup token file: {error.Message}");
            }

            return _secret;
        }
    }

    /// <summary>
    /// Is this attempt allowed to create the first administrator?
    ///
    /// Constant-time comparison, because a secret compared with an early-exit equality check leaks
    /// its prefix to anyone who can time the response — and this endpoint is rate-limited per IP,
    /// not per attempt across IPs.
    /// </summary>
    public static SetupAdmission Admit(string? presented)
    {
        lock (Gate)
        {
            if (_consumed) return SetupAdmission.AlreadyComplete;
            if (_secret.Length == 0) return SetupAdmission.Admitted;
            if (string.IsNullOrWhiteSpace(presented)) return SetupAdmission.SecretRequired;

            var a = System.Text.Encoding.UTF8.GetBytes(_secret);
            var b = System.Text.Encoding.UTF8.GetBytes(presented.Trim());
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b)
                ? SetupAdmission.Admitted
                : SetupAdmission.SecretWrong;
        }
    }

    /// <summary>
    /// Spend the secret. Called ONLY after the administrator row is committed.
    ///
    /// Order matters and is the whole point: consuming before the insert would leave a deployment
    /// with no administrator and no way to make one. Consuming after means a failed insert can be
    /// retried with the same token.
    /// </summary>
    public static void Consume()
    {
        lock (Gate)
        {
            _consumed = true;
            _secret = "";
            if (_secretPath.Length > 0)
            {
                try { File.Delete(_secretPath); } catch { /* best effort; the secret is dead either way */ }
                _secretPath = "";
            }
        }
    }

    /// <summary>Test seam. Restores the unarmed state; never called from production code.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) { _secret = ""; _consumed = false; _secretPath = ""; }
    }

    private static void HardenFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;   // ACL inheritance; the directory is the boundary
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* best effort — an exotic filesystem must not stop the process starting */ }
    }
}
