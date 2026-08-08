using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The readiness surface tells the truth about all twelve roles. v3.8.32.
///
/// This ladder shipped in v3.8.26 inside the <c>/colony/registry</c> route lambda and was WRONG for
/// half the roster from the day it was written. It ran over every contract key and asked two
/// questions that only mean something about a gated specialist:
///
/// <list type="bullet">
/// <item><c>ActivationTiers.Admits</c> returns false for a core ant at the <c>core</c> tier — the
///   tier enumerates SPECIALISTS it admits, and core ants are not in that set.</item>
/// <item><c>SpecialistGateOpen</c>'s switch had no arm for a core ant, so its default returned false
///   — "researcher has no rollout flag" and "researcher's flag is off" were the same value.</item>
/// </list>
///
/// So the six ants that are ALWAYS on could report <c>ready: false</c>, blocked by flags that do not
/// exist, on the exact surface an operator reads to decide what to switch on.
///
/// Nothing caught it because nothing could call it: the logic was a lambda inside an HTTP handler.
/// The extraction to <see cref="RoleReadiness"/> is what makes these assertions possible, and that
/// is the more durable half of the fix.
/// </summary>
[Collection("specialist-gates")]
public class RoleReadinessTests : IDisposable
{
    private readonly bool _specialistsWas = AnthillRuntime.EnableSpecialistAntExecution;
    private readonly ActivationTier _tierWas = AnthillRuntime.ActivationTier;
    private readonly bool _testerWas = AnthillRuntime.EnableTesterAnt;
    private readonly bool _soldierWas = AnthillRuntime.EnableSoldierAnt;
    private readonly bool _medicWas = AnthillRuntime.EnableMedicAnt;
    private readonly bool _archivistWas = AnthillRuntime.EnableArchivistAnt;
    private readonly bool _cartographerWas = AnthillRuntime.EnableUiCartographerAnt;
    private readonly bool _scribeWas = AnthillRuntime.EnableScribeAnt;

    public void Dispose()
    {
        AnthillRuntime.EnableSpecialistAntExecution = _specialistsWas;
        AnthillRuntime.ActivationTier = _tierWas;
        AnthillRuntime.EnableTesterAnt = _testerWas;
        AnthillRuntime.EnableSoldierAnt = _soldierWas;
        AnthillRuntime.EnableMedicAnt = _medicWas;
        AnthillRuntime.EnableArchivistAnt = _archivistWas;
        AnthillRuntime.EnableUiCartographerAnt = _cartographerWas;
        AnthillRuntime.EnableScribeAnt = _scribeWas;
        // Leave the shared snapshot consistent with the restored flags. It is process-global, and a
        // snapshot built under this class's settings would otherwise leak into whatever runs next.
        AntExecutorCatalog.Initialize(AllRoles());
    }

    private static List<string> AllRoles() => AntExecutionCatalog.Contracts.Keys.ToList();

    /// <summary>
    /// Set the gates, THEN rebuild the snapshot.
    ///
    /// Order matters and is easy to get wrong: <c>AntExecutorCatalog.Snapshot</c> is cached by
    /// <c>Initialize</c>, while <c>AntRegistry.ExecutableRoleIds</c> is a property recomputed on
    /// every read. Configuring after initialising would leave the two disagreeing, and the test
    /// would be measuring a colony that never exists.
    /// </summary>
    private static void Configure(bool specialists, ActivationTier tier, bool rolloutFlags)
    {
        AnthillRuntime.EnableSpecialistAntExecution = specialists;
        AnthillRuntime.ActivationTier = tier;
        AnthillRuntime.EnableTesterAnt = rolloutFlags;
        AnthillRuntime.EnableSoldierAnt = rolloutFlags;
        AnthillRuntime.EnableMedicAnt = rolloutFlags;
        AnthillRuntime.EnableArchivistAnt = rolloutFlags;
        AnthillRuntime.EnableUiCartographerAnt = rolloutFlags;
        AnthillRuntime.EnableScribeAnt = rolloutFlags;
        AntExecutorCatalog.Initialize(AllRoles());
    }

    /// <summary>Every tool any contract declares, so these tests isolate the GATE rung. Tool
    /// starvation is asserted separately below, or that isolation would prove nothing.</summary>
    private static IReadOnlyCollection<string> AllDeclaredTools() =>
        AntExecutionCatalog.Contracts.Values.SelectMany(c => c.AllowedTools)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> CoreRoles() =>
        AllRoles().Where(r => !AntExecutorCatalog.IsGated(r)).OrderBy(r => r, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The gated set, pinned by name. Everything else derives from it, so if it drifts the tests
    /// below would quietly change meaning instead of failing.
    /// </summary>
    [Fact]
    public void ExactlySixRoles_AreGated()
    {
        Assert.Equal(
            new[] { "archivist", "medic", "scribe", "soldier", "tester", "ui_cartographer" },
            AntExecutorCatalog.GatedRoles.OrderBy(r => r, StringComparer.Ordinal).ToArray());

        Assert.Equal(6, CoreRoles().Count);
    }

    /// <summary>
    /// THE regression. At the narrowest configuration — tier `core`, specialist execution off — the
    /// six core ants must still be ready. That is the configuration every existing installation
    /// runs, and before v3.8.32 all six reported
    /// "activation tier 'core' does not admit this role".
    /// </summary>
    [Fact]
    public void AtTheNarrowestConfiguration_TheSixCoreAnts_AreReady()
    {
        Configure(specialists: false, ActivationTier.Core, rolloutFlags: false);

        var rows = RoleReadiness.ForAllRoles(AllDeclaredTools(), grantedCapabilities: null);

        foreach (var role in CoreRoles())
        {
            var row = rows.Single(r => r.RoleId == role);
            Assert.True(row.Ready, $"core ant '{role}' reported not ready: {row.BlockedReason}");
        }
    }

    /// <summary>
    /// A core ant is never DESCRIBED by a gate either. Asserted on the reason text as well as the
    /// flags, because the text is what an operator reads and the text was what lied.
    /// </summary>
    [Fact]
    public void ACoreAnt_IsNeverBlockedByAGateItDoesNotHave()
    {
        Configure(specialists: false, ActivationTier.Core, rolloutFlags: false);

        var rows = RoleReadiness.ForAllRoles(AllDeclaredTools(), grantedCapabilities: null);

        foreach (var role in CoreRoles())
        {
            var row = rows.Single(r => r.RoleId == role);

            Assert.False(row.Gated);
            Assert.Equal(RoleGateStatus.NotGated, row.GateStatus);
            Assert.True(row.GateOpen);
            Assert.True(row.AdmittedByTier);
            Assert.DoesNotContain("rollout flag", row.BlockedReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("activation tier", row.BlockedReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("colony-wide", row.BlockedReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// ...and the gates still BIND on the roles they govern. Without this, the fix above could have
    /// been "stop checking gates", which would be worse than the bug.
    /// </summary>
    [Fact]
    public void AGatedSpecialist_IsStillBlockedWhenItsGateIsShut()
    {
        Configure(specialists: false, ActivationTier.Core, rolloutFlags: false);

        var rows = RoleReadiness.ForAllRoles(AllDeclaredTools(), grantedCapabilities: null);

        foreach (var role in AntExecutorCatalog.GatedRoles)
        {
            var row = rows.Single(r => r.RoleId == role);
            Assert.True(row.Gated);
            Assert.False(row.Ready);
            Assert.NotEqual("", row.BlockedReason);
        }
    }

    /// <summary>The three gates report DISTINCT reasons, in the order the runtime hits them.</summary>
    [Fact]
    public void EachGate_ReportsItsOwnReason()
    {
        Configure(specialists: false, ActivationTier.Full, rolloutFlags: true);
        Assert.Equal(RoleGateStatus.ClosedByMasterSwitch, AntExecutorCatalog.GateStatusOf("tester"));

        Configure(specialists: true, ActivationTier.Core, rolloutFlags: true);
        Assert.Equal(RoleGateStatus.ClosedByTier, AntExecutorCatalog.GateStatusOf("tester"));

        Configure(specialists: true, ActivationTier.Full, rolloutFlags: false);
        Assert.Equal(RoleGateStatus.ClosedByRolloutFlag, AntExecutorCatalog.GateStatusOf("tester"));

        Configure(specialists: true, ActivationTier.Full, rolloutFlags: true);
        Assert.Equal(RoleGateStatus.Open, AntExecutorCatalog.GateStatusOf("tester"));
    }

    /// <summary>
    /// With the full roster switched on, no role is blocked BY A GATE. Stated about gates rather
    /// than about overall readiness, because a role can still be legitimately blocked by a tool this
    /// process did not register, and conflating the two is what the original defect did.
    /// </summary>
    [Fact]
    public void UnderTheFullRoster_NoRoleIsBlockedByAGate()
    {
        Configure(specialists: true, ActivationTier.Full, rolloutFlags: true);

        var rows = RoleReadiness.ForAllRoles(AllDeclaredTools(), grantedCapabilities: null);

        Assert.All(rows, r => Assert.True(r.GateOpen, $"{r.RoleId}: {r.GateStatus}"));
        Assert.All(rows, r => Assert.True(r.AdmittedByTier, r.RoleId));
    }

    /// <summary>
    /// Tool starvation still blocks. The tests above hand the ladder every declared tool, so without
    /// this one they would pass against an implementation that never checked tools at all.
    /// </summary>
    [Fact]
    public void ARoleWhoseToolsAreNotRegistered_IsBlockedAndSaysWhich()
    {
        Configure(specialists: true, ActivationTier.Full, rolloutFlags: true);

        var rows = RoleReadiness.ForAllRoles(Array.Empty<string>(), grantedCapabilities: null);

        foreach (var row in rows.Where(r => r.DeclaredTools.Count > 0))
        {
            Assert.False(row.Ready);
            Assert.Contains("not registered", row.BlockedReason, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(row.UnregisteredTools);
        }
    }

    /// <summary>
    /// An ungranted capability blocks too, and is reported AFTER tools — the runtime resolves a tool
    /// before it checks what the tool may do, and the reason shown should match the order an
    /// operator would hit them.
    /// </summary>
    [Fact]
    public void AnUngrantedCapability_BlocksOnceToolsAreRegistered()
    {
        Configure(specialists: true, ActivationTier.Full, rolloutFlags: true);

        var rows = RoleReadiness.ForAllRoles(AllDeclaredTools(),
            grantedCapabilities: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        foreach (var row in rows.Where(r => r.RequiredCapabilities.Count > 0))
        {
            Assert.False(row.Ready);
            Assert.Contains("cannot grant", row.BlockedReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Every contracted role appears exactly once. A readiness surface that silently omits
    /// a role is the failure mode that hid the archivist for the project's whole history.</summary>
    [Fact]
    public void EveryContractedRole_AppearsExactlyOnce()
    {
        Configure(specialists: true, ActivationTier.Full, rolloutFlags: true);

        var rows = RoleReadiness.ForAllRoles(AllDeclaredTools(), grantedCapabilities: null);

        Assert.Equal(AntExecutionCatalog.Contracts.Count, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.RoleId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
