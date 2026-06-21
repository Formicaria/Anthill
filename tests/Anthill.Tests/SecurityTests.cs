using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Security;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Security regressions ported from the Python hardening suite: token strength, constant-time
/// comparison, SSRF/local URL filtering, safe patch paths, workspace path confinement, the
/// failed-auth rate limiter, and the AES-GCM field cipher introduced in the .NET migration.
/// </summary>
public class SecurityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("change-me-local-token")]
    [InlineData("too-short")]
    [InlineData("  leading-and-trailing-whitespace-32chars  ")]
    public void WeakTokens_AreRejected(string token)
    {
        Assert.Throws<AnthillSecurityException>(() => TokenSecurity.ValidateApiTokenStrength(token));
    }

    [Fact]
    public void StrongToken_IsAccepted()
    {
        var token = TokenSecurity.GenerateStrongToken();
        Assert.True(token.Length >= 32);
        TokenSecurity.ValidateApiTokenStrength(token); // should not throw
    }

    [Fact]
    public void ConstantTimeEquals_MatchesAndRejects()
    {
        Assert.True(TokenSecurity.ConstantTimeEquals("abc123", "abc123"));
        Assert.False(TokenSecurity.ConstantTimeEquals("abc123", "abc124"));
        Assert.False(TokenSecurity.ConstantTimeEquals("abc", "abcdef"));
    }

    [Theory]
    [InlineData("http://127.0.0.1/x", true)]
    [InlineData("http://localhost/x", true)]
    [InlineData("http://10.0.0.5/x", true)]
    [InlineData("http://192.168.1.1/", true)]
    [InlineData("ftp://example.com/x", true)]
    [InlineData("http://service.local/x", true)]
    [InlineData("https://docs.python.org/3/", false)]
    [InlineData("https://github.com/anthropics", false)]
    public void OutboundUrlFilter_BlocksPrivateAndLocalTargets(string url, bool blocked)
    {
        Assert.Equal(blocked, UrlSafety.IsBlockedOutboundUrl(url));
    }

    [Fact]
    public void SafePatchPath_BlocksTraversalAndBlockedDirs()
    {
        Assert.Throws<ArgumentException>(() => Validation.ValidateSafePatchPath("../etc/passwd"));
        Assert.Throws<ArgumentException>(() => Validation.ValidateSafePatchPath("/abs/path.txt"));
        Assert.Throws<ArgumentException>(() => Validation.ValidateSafePatchPath(".git/config"));
        Assert.Throws<ArgumentException>(() => Validation.ValidateSafePatchPath("data/anthill.db"));
        Assert.Equal("notes/todo.md", Validation.ValidateSafePatchPath("notes/todo.md"));
    }

    [Fact]
    public void WorkspaceGuard_ConfinesToRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "anthill_ws_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var guard = new WorkspacePathGuard(root);
        var inside = guard.ResolveSafePath("sub/file.txt");
        Assert.StartsWith(root, inside);
        Assert.Throws<UnauthorizedAccessException>(() => guard.ResolveSafePath("../escape.txt"));
    }

    [Fact]
    public void FailedAuthLimiter_TripsAfterMaxAttempts()
    {
        var limiter = new RateLimiter(60, 3);
        const string ip = "203.0.113.7";
        Assert.False(limiter.IsLimited(ip));
        limiter.RecordAttempt(ip);
        limiter.RecordAttempt(ip);
        limiter.RecordAttempt(ip);
        Assert.True(limiter.IsLimited(ip));
        limiter.Clear(ip); // successful auth clears the bucket
        Assert.False(limiter.IsLimited(ip));
    }

    [Fact]
    public void FieldCipher_RoundTripsAndPassesThroughPlaintext()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var cipher = new FieldCipher(key, enabled: true);
        const string secret = "def patch():\n    return token=SUPERSECRET";
        var sealedValue = cipher.Protect(secret);
        Assert.NotNull(sealedValue);
        Assert.StartsWith("enc:v1:", sealedValue);
        Assert.NotEqual(secret, sealedValue);
        Assert.Equal(secret, cipher.Unprotect(sealedValue));
        // Legacy plaintext (no enc:v1 prefix) passes through untouched.
        Assert.Equal("legacy", cipher.Unprotect("legacy"));
    }

    [Fact]
    public void FieldCipher_WrongKey_DoesNotReturnGarbage()
    {
        var cipher = new FieldCipher(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), enabled: true);
        var sealedValue = cipher.Protect("payload");
        var other = new FieldCipher(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), enabled: true);
        var result = other.Unprotect(sealedValue);
        Assert.Contains("could not be decrypted", result);
    }
}
