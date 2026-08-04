using System.Text.Json;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v1.10.0 regression tests for the Patch Center "Apply" 403 bug: the API capability gate
/// ApiPermissions["apply_patch"] shipped as a static false and was never projected from
/// patch_application_enabled, so POST /apply/{id} always answered permission_denied even after the
/// operator enabled patch application in Settings. The gate must follow the setting through both
/// boot-time projection and live settings updates.
/// </summary>
[Collection("Autonomy")] // serialize with the other tests that mutate AnthillRuntime globals
public class PatchApplyGateTests : IDisposable
{
    private readonly bool _saved;

    public PatchApplyGateTests()
    {
        AnthillRuntime.Initialize();
        _saved = AnthillRuntime.EnablePatchApplication;
    }

    public void Dispose() => Set(_saved); // restore via the same public path so gate+config stay consistent

    private static void Set(bool enabled) => AnthillRuntime.ApplySettingsUpdate(
        new Dictionary<string, JsonElement> { ["patch_application_enabled"] = JsonSerializer.SerializeToElement(enabled) });

    [Fact]
    public void ApplyPatchCapabilityGate_FollowsPatchApplicationEnabled()
    {
        Set(true);
        Assert.True(AnthillRuntime.EnablePatchApplication);
        Assert.True(AnthillRuntime.ApiPermissions["apply_patch"],
            "apply_patch capability gate must open when patch_application_enabled=true (the v1.10.0 Patch Center 403 fix)");

        Set(false);
        Assert.False(AnthillRuntime.EnablePatchApplication);
        Assert.False(AnthillRuntime.ApiPermissions["apply_patch"],
            "apply_patch capability gate must close again when patch application is disabled");
    }

    [Fact]
    public void HomelabGates_AreOperatorEditableAndInSettingsSnapshot()
    {
        // v1.10.0: homelab toggles are editable from the console and visible in the snapshot,
        // so the new Homelab page can be enabled without hand-editing config.json.
        Assert.Contains("homelab_enabled", AnthillRuntime.EditableSettingKeys);
        Assert.Contains("homelab_scheduler_enabled", AnthillRuntime.EditableSettingKeys);
        Assert.Contains("homelab_mock_providers_enabled", AnthillRuntime.EditableSettingKeys);
        Assert.Contains("homelab_max_concurrent_checks", AnthillRuntime.EditableSettingKeys);

        var snap = AnthillRuntime.SettingsSnapshot();
        Assert.True(snap.ContainsKey("homelab_enabled"));
        Assert.True(snap.ContainsKey("homelab_scheduler_enabled"));
        Assert.True(snap.ContainsKey("homelab_mock_providers_enabled"));
        Assert.True(snap.ContainsKey("homelab_max_concurrent_checks"));
    }

    /// <summary>
    /// v3.7.2 — operator-defined tools must be reachable from the console.
    ///
    /// v3.4.1 shipped the whole subsystem with its gate missing from the editable set, so the only
    /// way to switch it on was hand-editing config.json and restarting. The console could list
    /// stored definitions and report them rejected, and offered no way to enable the feature that
    /// would let any of them register — "shipped but unreachable" one layer below the endpoints,
    /// and indistinguishable from the feature simply being broken.
    ///
    /// Both keys, not just the flag: the host allow-list IS the safety boundary here, and an
    /// operator who can turn the feature on but cannot name a host has every definition rejected
    /// with no way to fix it.
    /// </summary>
    [Fact]
    public void UserDefinedToolGates_AreOperatorEditableAndInSettingsSnapshot()
    {
        Assert.Contains("user_tools_enabled", AnthillRuntime.EditableSettingKeys);
        Assert.Contains("user_tool_allowed_hosts", AnthillRuntime.EditableSettingKeys);

        var snap = AnthillRuntime.SettingsSnapshot();
        Assert.True(snap.ContainsKey("user_tools_enabled"));
        Assert.True(snap.ContainsKey("user_tool_allowed_hosts"));
    }

    /// <summary>
    /// And the gate must actually MOVE. Being in the whitelist is not the same as being applied —
    /// that distinction is exactly what this file was written to defend for apply_patch.
    /// </summary>
    [Fact]
    public void EnablingUserTools_MovesTheRuntimeGate()
    {
        var saved = AnthillRuntime.EnableUserTools;
        try
        {
            SetUserTools(true);
            Assert.True(AnthillRuntime.EnableUserTools);

            SetUserTools(false);
            Assert.False(AnthillRuntime.EnableUserTools);
        }
        // Restored through the same public path, so the runtime gate and the persisted config
        // cannot end up disagreeing with each other.
        finally { SetUserTools(saved); }
    }

    private static void SetUserTools(bool enabled) => AnthillRuntime.ApplySettingsUpdate(
        new Dictionary<string, JsonElement> { ["user_tools_enabled"] = JsonSerializer.SerializeToElement(enabled) });
}
