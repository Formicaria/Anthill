using Anthill.Core.Configuration;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.12 — pins the property that made this move risky rather than the fact that it compiled.
///
/// <c>UrlSafety</c> and <c>Validation</c> now live in the SDK, which cannot see
/// <c>AnthillRuntime</c>. The bridge is a settable default installed by a module initializer in
/// <c>Anthill.Core</c>. The failure mode that design has to rule out is a guard that stops tracking
/// the mutable statics behind it — a blocklist edited after startup that the guard never sees. That
/// would break nothing at rest, because the SDK's built-in fallbacks are identical to the core's
/// declared defaults, so only a test like this one can tell the two apart.
/// </summary>
public class SafetyPolicyTests
{
    [Fact]
    public void CoreInstallsLiveReadersWithoutAnyCompositionRoot()
    {
        // No colony, no host, no Configure call anywhere in this test — the module initializer alone.
        Assert.Same(SsrfRuntime.Live, SafetyPolicy.Ssrf);
        Assert.Same(ToolRuntime.Live, SafetyPolicy.ToolOptions);
    }

    [Fact]
    public void HostBlockedAfterFirstUseIsHonouredOnTheNextCall()
    {
        const string host = "internal-metadata.example";
        var url = $"http://{host}/latest";

        // Touch the guard first, so any caching would already have happened.
        Assert.False(UrlSafety.IsBlockedOutboundUrl(url));

        AnthillRuntime.SsrfBlockedHostnames.Add(host);
        try
        {
            Assert.True(UrlSafety.IsBlockedOutboundUrl(url));
        }
        finally
        {
            AnthillRuntime.SsrfBlockedHostnames.Remove(host);
        }

        Assert.False(UrlSafety.IsBlockedOutboundUrl(url));
    }

    [Fact]
    public void PathPartBlockedAfterFirstUseIsHonouredOnTheNextCall()
    {
        const string part = "vendored";
        var path = $"{part}/notes.md";

        Assert.Equal(path, Validation.ValidateSafePatchPath(path));

        AnthillRuntime.BlockedPathParts.Add(part);
        try
        {
            Assert.Throws<ArgumentException>(() => Validation.ValidateSafePatchPath(path));
        }
        finally
        {
            AnthillRuntime.BlockedPathParts.Remove(part);
        }

        Assert.Equal(path, Validation.ValidateSafePatchPath(path));
    }

    [Fact]
    public void ExplicitOptionsOverrideTheInstalledDefault()
    {
        // What a module-supplied tool will do once the implementations move out in 5c step 4.
        var policy = new FixedSsrfPolicy("blocked.test");
        Assert.True(UrlSafety.IsBlockedOutboundUrl("https://blocked.test/x", policy));
        Assert.False(UrlSafety.IsBlockedOutboundUrl("https://blocked.test/x"));
    }

    [Fact]
    public void ConstCapsAreOneDeclaration()
    {
        Assert.Equal(Validation.ApprovalIdMaxChars, AnthillRuntime.ApprovalIdMaxChars);
        Assert.Equal(Validation.PatchIdMaxChars, AnthillRuntime.PatchIdMaxChars);
        Assert.Equal(Validation.SourceIdMaxChars, AnthillRuntime.SourceIdMaxChars);
    }

    private sealed class FixedSsrfPolicy(params string[] hosts) : Anthill.SDK.Security.ISsrfPolicy
    {
        public IReadOnlySet<string> BlockedHostnames { get; } =
            new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> BlockedHostSuffixes { get; } = Array.Empty<string>();
    }
}
