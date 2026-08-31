using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Memory;
using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Events;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Acceptance gates 1 and 2, the last two of the twelve. v0.3.8.80 (PLAN.md §3).
///
/// GATE 1: all twelve roles report Ready under the full profile.
/// GATE 2: every enabled role has a handler, a contract, a real production trigger and typed output.
///
/// WHY THESE WERE HELD FOR R3. Both depend on the roster being genuinely complete rather than
/// nominally so, and until v0.3.8.76 the contracts disagreed with the runtime about which roles
/// could even call a model. A "Ready" computed from declarations that were wrong is a green light
/// with nothing behind it — which is why the plan tied these to the release line that made the
/// declarations true rather than to the one that first wrote them down.
///
/// WHAT WAS ALREADY PROVED, and what it left out. `RoleReadinessTests.UnderTheFullRoster_NoRoleIsBlockedByAGate`
/// asserts no role is blocked BY A GATE. That is one of five reasons `RoleReadiness` can withhold
/// Ready — the others being a missing handler, an unregistered tool, an ungranted capability, and a
/// runtime that reports itself unavailable. A role can pass every gate and still be unready, and
/// "no gate blocks it" reads exactly like "it is ready" in a summary. Gate 1 asks for Ready.
/// </summary>
[Collection("specialist-gates")]
public class AcceptanceGatesOneAndTwoTests : IDisposable
{
    private readonly string _dir;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    // Captured because this fixture mutates it. `RosterGates` covers the roster switches and not
    // this one, and a process-wide flag left on by a test that finished is the leak
    // ModelRoutingGlobalsTests exists to stop one register over.
    private readonly bool _fileToolsWere = AnthillRuntime.EnableFileTools;

    public AcceptanceGatesOneAndTwoTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-gates-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        RosterGates.Restore(_gatesWere);
        AnthillRuntime.EnableFileTools = _fileToolsWere;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// THE FULL PROFILE: every specialist on, the widest activation tier, the real tool registry, and
    /// an operator who granted every capability the contracts ask for.
    ///
    /// The tools come from the RUNTIME rather than from the contracts' own `AllowedTools`, and that
    /// distinction is the point. Deriving the registry from the declarations would make this test
    /// assert that the contracts agree with themselves — it would pass for a role declaring a tool
    /// nobody implemented, which is exactly the state `RoleReadiness.UnregisteredTools` exists to
    /// report. The registry is the runtime's answer; the contract is the claim.
    ///
    /// AND THE RUNTIME'S ANSWER IS THE QUEEN'S REGISTRY, NOT THE MODULE'S. The first draft used
    /// `ToolsModule.ContributedTools` alone and six roles came back unready — `system_info`,
    /// `search_workspace`, `repository_index`, `run_allowlisted_check`, `read_changed_files_summary`
    /// all reported unregistered. None of that was a gate failure. Since v3.8.16 the registry is
    /// assembled in TWO halves: `Queen.BuildToolRegistry` constructs the core tools, and the
    /// composition root drains the module's six into the same registry afterwards. A fixture that
    /// takes one half measures a colony nobody runs — and would have reported gate 1 as unmet
    /// against a runtime that meets it, which is a false negative in the one direction that wastes
    /// the most time.
    /// </summary>
    private static List<RoleReadinessRow> ReadinessUnderTheFullProfile(string dir)
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = ActivationTier.Full;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableSoldierAnt = true;
        AnthillRuntime.EnableMedicAnt = true;
        AnthillRuntime.EnableArchivistAnt = true;
        AnthillRuntime.EnableUiCartographerAnt = true;
        AnthillRuntime.EnableScribeAnt = true;

        // `search_workspace` is registered only when file tools are on, so the full profile has
        // them on — an operator who disabled them has a narrower colony, which is a different
        // question from whether this one can be fully ready.
        AnthillRuntime.EnableFileTools = true;

        using var memory = new SqliteMemory(Path.Combine(dir, "gates.db"));
        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard()));

        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);

        // v0.3.8.102 — the THIRD half of the registry: the system-action tools are adopted by the
        // API host where the homelab executor is built, so a full composition includes them the
        // same way it includes the tools module. Composed here over the module's own deterministic
        // pieces (a real repository, the mock runner) — the same fixture-versus-runtime rule the
        // header states: gate 1 measures the colony production composes, not a half of it.
        var homelab = new Anthill.Modules.Homelab.HomelabRepository(Path.Combine(dir, "gates-homelab.db"));
        var homelabExecutor = new Anthill.Modules.Homelab.Actions.ActionExecutor(
            homelab, new Anthill.Modules.Homelab.Actions.IHomelabActionRunner[]
                { new Anthill.Modules.Homelab.Actions.MockActionRunner() }, isStopped: () => false);
        queen.AdoptModuleTools(Anthill.Modules.Homelab.Actions.SystemActionTools.For(
            homelabExecutor, _ => null));

        // v0.3.8.103 — and the send lane, for exactly the reason the operation lane is here: gate 1
        // measures the colony PRODUCTION composes. The API host registers these beside the module
        // tools, so a fixture that omitted them would report the tester unready for tools the real
        // colony has. The adapter is handed an empty destination map — the shipped default — because
        // readiness is about whether the tools are REGISTERED, not about what an operator has
        // configured them to reach.
        queen.AdoptModuleTools(Anthill.Modules.Tools.ExternalActionTools.For(
            new Anthill.Modules.Tools.ConfiguredWebhookAdapter(
                () => new Dictionary<string, string>(), () => new HttpClient()),
            _ => null));

        var registered = queen.Tools.Names.ToList();

        // Every capability any contract requires — the operator who said yes to everything. A
        // narrower grant is a legitimate deployment and a different question; gate 1 is about
        // whether the colony CAN be fully ready, not whether every deployment is.
        var granted = AntExecutionCatalog.Contracts.Values
            .SelectMany(c => c.RequiredCapabilities)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return RoleReadiness.ForAllRoles(registered, granted);
    }

    // -----------------------------------------------------------------------------------------------
    // Gate 1
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// ALL TWELVE REPORT READY. The failure message names each role and the reason it withheld,
    /// because `Ready` is a conjunction of five conditions and the boolean identifies none of them —
    /// the same lesson the mission-evaluation assertions learned two releases ago.
    /// </summary>
    [Fact]
    public void AcceptanceGateOne_AllTwelveRolesReportReady_UnderTheFullProfile()
    {
        var rows = ReadinessUnderTheFullProfile(_dir);

        Assert.Equal(12, rows.Count);

        var blocked = rows.Where(r => !r.Ready)
            .Select(r => $"\n  {r.RoleId}: {r.BlockedReason}"
                       + (r.UnregisteredTools.Count > 0
                            ? $"\n      unregistered tools: {string.Join(", ", r.UnregisteredTools)}" : "")
                       + (r.UngrantedCapabilities.Count > 0
                            ? $"\n      ungranted capabilities: {string.Join(", ", r.UngrantedCapabilities)}" : ""))
            .ToList();

        Assert.True(blocked.Count == 0,
            $"acceptance gate 1 is not met: {blocked.Count} of {rows.Count} roles are not Ready under "
          + "the full profile." + string.Join("", blocked)
          + "\n\nThe colony is not a twelve-role colony until every one of these reports Ready.");
    }

    /// <summary>
    /// And Ready is not vacuous: the same computation WITHHOLDS Ready when a declared tool is not
    /// registered. Without this, gate 1 would pass just as well against a readiness function that
    /// returned true unconditionally.
    /// </summary>
    [Fact]
    public void AcceptanceGateOne_IsNotVacuous_AnUnregisteredToolStillWithholdsReady()
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = ActivationTier.Full;

        var granted = AntExecutionCatalog.Contracts.Values
            .SelectMany(c => c.RequiredCapabilities).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = RoleReadiness.ForAllRoles(Array.Empty<string>(), granted);

        Assert.Contains(rows, r => !r.Ready && r.UnregisteredTools.Count > 0);
    }

    // -----------------------------------------------------------------------------------------------
    // Gate 2
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// EVERY ENABLED ROLE HAS A HANDLER, A CONTRACT, A REAL TRIGGER AND TYPED OUTPUT.
    ///
    /// Each clause is read from a different source on purpose, because the gate is about them
    /// AGREEING: the handler from the runtime snapshot, the contract from the catalog, the trigger
    /// from the declared scheduling mode, and the typed output from the artifact types the contract
    /// promises to produce. A role satisfying three of the four is a role that will be dispatched and
    /// produce something nothing downstream can consume.
    /// </summary>
    [Fact]
    public void AcceptanceGateTwo_EveryRoleHasAHandlerContractTriggerAndTypedOutput()
    {
        var rows = ReadinessUnderTheFullProfile(_dir);
        var problems = new List<string>();

        foreach (var row in rows)
        {
            var contract = AntExecutionCatalog.ContractFor(row.RoleId)!;

            if (!row.HandlerPresent)
                problems.Add($"{row.RoleId}: no runtime handler");

            if (string.IsNullOrWhiteSpace(row.ContractVersion))
                problems.Add($"{row.RoleId}: contract has no version");

            // The TRIGGER. A declared scheduling mode is what says how the role is reached in
            // production: planner-selected, policy-inserted, failure-triggered or post-finalization.
            // The enum has no "none", so the check is that it is declared and that the runtime
            // agrees — `RoleReadiness` reads it from the contract, so a disagreement here would be a
            // contract that changed without the readiness view following.
            if (string.IsNullOrWhiteSpace(row.SchedulingMode))
                problems.Add($"{row.RoleId}: no scheduling mode, so nothing states how it is reached");
            else if (row.SchedulingMode != contract.Scheduling.ToString())
                problems.Add($"{row.RoleId}: readiness reports scheduling '{row.SchedulingMode}' and "
                           + $"the contract declares '{contract.Scheduling}'");

            // TYPED OUTPUT. A role producing nothing typed cannot be consumed by the artifact
            // channel, and its work reaches the next role only as prose — the control channel this
            // repository spent three releases removing.
            if (contract.ProducedArtifactTypes.Count == 0)
                problems.Add($"{row.RoleId}: declares no produced artifact type");
        }

        Assert.True(problems.Count == 0,
            $"acceptance gate 2 is not met ({problems.Count} problem(s)):\n  "
          + string.Join("\n  ", problems));
    }

    /// <summary>
    /// The gate covers the roster the runtime HAS. A twelfth role added without a contract would
    /// otherwise be absent from `rows` entirely and the gate would pass by not seeing it — the same
    /// shape as the adapter matrix's completeness check.
    /// </summary>
    [Fact]
    public void AcceptanceGateTwo_CoversEveryExecutableRole()
    {
        var rows = ReadinessUnderTheFullProfile(_dir).Select(r => r.RoleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = AntRegistry.ExecutableRoleIds.Where(r => !rows.Contains(r)).ToList();

        Assert.True(missing.Count == 0,
            "these executable roles have no readiness row, so gates 1 and 2 never examined them: "
          + string.Join(", ", missing));
    }
}
