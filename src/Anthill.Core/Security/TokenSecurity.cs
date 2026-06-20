using System.Security.Cryptography;
using System.Text;
using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Security;

/// <summary>Raised when the API is asked to start with an unsafe security posture.</summary>
public sealed class AnthillSecurityException : Exception
{
    public AnthillSecurityException(string message) : base(message) { }
}

/// <summary>
/// API token strength enforcement and constant-time comparison.
///
/// The API must fail loudly at boot rather than silently accept weak credentials:
/// empty, whitespace-wrapped, default-placeholder, or sub-32-char tokens are rejected.
/// Token comparison uses <see cref="CryptographicOperations.FixedTimeEquals"/> so an
/// attacker cannot learn the token a byte at a time through timing.
/// </summary>
public static class TokenSecurity
{
    public static void ValidateApiTokenStrength(string? token)
    {
        token ??= "";
        if (token.Length == 0)
            throw new AnthillSecurityException(
                "\n\n[ANTHILL SECURITY] API refused to start.\n" +
                "  ANTHILL_API_TOKEN is empty or unset.\n" +
                "  Set a strong token before starting the API.\n");

        if (token.Trim() != token)
            throw new AnthillSecurityException(
                "\n\n[ANTHILL SECURITY] API refused to start.\n" +
                "  ANTHILL_API_TOKEN must not contain leading or trailing whitespace.\n");

        if (ConstantTimeEquals(token, AnthillRuntime.ApiTokenDefaultPlaceholder))
            throw new AnthillSecurityException(
                "\n\n[ANTHILL SECURITY] API refused to start.\n" +
                "  ANTHILL_API_TOKEN is still set to the default placeholder value.\n" +
                "  Set a strong token before starting the API:\n\n" +
                "      export ANTHILL_API_TOKEN=\"<your-random-token-here>\"\n\n" +
                "  The token must be at least 32 characters long.\n");

        if (token.Length < AnthillRuntime.ApiTokenMinLength)
            throw new AnthillSecurityException(
                $"\n\n[ANTHILL SECURITY] API refused to start.\n" +
                $"  ANTHILL_API_TOKEN is only {token.Length} characters long.\n" +
                $"  A minimum of {AnthillRuntime.ApiTokenMinLength} characters is required.\n");
    }

    /// <summary>Validate API bind/auth choices before the server accepts requests.</summary>
    public static void ValidateApiRuntimeSecurity()
    {
        if (AnthillRuntime.EnableApiAuth)
            ValidateApiTokenStrength(AnthillRuntime.ApiAuthToken);

        if (!AnthillRuntime.EnableApiAuth && !UrlSafety.IsLoopbackBindHost(AnthillRuntime.ApiHost))
            throw new AnthillSecurityException(
                "\n\n[ANTHILL SECURITY] API refused to start.\n" +
                "  API auth is disabled while api_host is not loopback-only.\n" +
                "  Keep api_auth_enabled=true, or bind api_host to 127.0.0.1/localhost/::1.\n");
    }

    public static bool ConstantTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a ?? "");
        var bb = Encoding.UTF8.GetBytes(b ?? "");
        // FixedTimeEquals short-circuits on length, so pad to equal length first to
        // avoid leaking the token length through timing on mismatched inputs.
        if (ba.Length != bb.Length)
        {
            var max = Math.Max(ba.Length, bb.Length);
            Array.Resize(ref ba, max);
            Array.Resize(ref bb, max);
            return false & CryptographicOperations.FixedTimeEquals(ba, bb);
        }
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    /// <summary>Generates a cryptographically strong, URL-safe token suitable for ANTHILL_API_TOKEN.</summary>
    public static string GenerateStrongToken(int bytes = 32)
    {
        var raw = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(raw).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
