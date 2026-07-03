using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v1.8.21 — the new <c>autonomy_autoapply_keep_without_verify</c> gate must round-trip through the
/// runtime and appear in the settings snapshot (so the console can read/write it). The gate lets a
/// deployment with no build toolchain keep auto-applied patches instead of the built-in
/// <c>dotnet build &amp;&amp; dotnet test</c> verify failing and rolling everything back.
/// </summary>
[Collection("Autonomy")]
public class AutoApplyConfigTests : IDisposable
{
    private readonly bool _saved;

    public AutoApplyConfigTests()
    {
        AnthillRuntime.Initialize();
        _saved = AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify;
    }

    public void Dispose() => AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify = _saved;

    [Fact]
    public void KeepWithoutVerify_IsExposedInSettingsSnapshot()
    {
        AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify = true;
        var snap = AnthillRuntime.SettingsSnapshot();
        Assert.True(snap.ContainsKey("autonomy_autoapply_keep_without_verify"));
        Assert.Equal(true, snap["autonomy_autoapply_keep_without_verify"]);

        AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify = false;
        Assert.Equal(false, AnthillRuntime.SettingsSnapshot()["autonomy_autoapply_keep_without_verify"]);
    }

    [Fact]
    public void KeepWithoutVerify_DefaultsOff_ForSafety()
    {
        // A fresh config must default to verifying (safe): the escape hatch is opt-in only.
        Assert.False(new AnthillConfig().AutonomyAutoApplyKeepWithoutVerify);
    }
}
