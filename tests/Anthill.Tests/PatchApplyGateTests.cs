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
}
