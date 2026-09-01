using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Pheromones;
using Anthill.Core.Tools;
using Anthill.SDK.Contracts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Stage F — the full-roster qualification fixture. v3.8.29.
///
/// The plan's requirement is precise and worth quoting: "CI should require all twelve to report Ready
/// under the full-roster qualification fixture." NOT "the default flips to full" — the default stays
/// `core`, and flipping it is an operator's deliberate act. What CI must guarantee is that when an
/// operator DOES flip it, every role can actually run.
///
/// This is the fixture. It resolves the roster exactly as `ProjectConfig` does, then asks each role
/// the same questions `/colony` reports — handler, contract, tools registered, capabilities grantable
/// — and fails naming the first binding reason.
///
/// Deliberately NOT a live mission. A test that ran twelve roles against a real model would be slow,
/// non-deterministic and dependent on a provider being up; it would fail for reasons that are not
/// about qualification and would be disabled within a month. What this proves is that nothing
/// STRUCTURAL stops the roster — which is the half a test can honestly own. The other half is a real
/// run, and that belongs to the operator.
/// </summary>
public class FullRosterQualificationTests
{
    private static readonly string[] TwelveRoles =
    {
        "researcher", "web", "file", "coder", "builder", "verifier",
        "ui_cartographer", "tester", "soldier", "scribe", "medic", "archivist",
    };

    /// <summary>The tools a fully-equipped colony registers — built-ins plus the Tools module.</summary>
    private static IReadOnlySet<string> FullyEquipped() =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system_info", "run_allowlisted_check", "search_workspace",
            "read_changed_files_summary", "repository_index",
            "list_directory", "read_text_file", "write_text_file",
            "web_search", "shell_command", "apply_patch",
            // v0.3.8.98 — the researcher's contract declares it, and a fixture that omitted it
            // would fail the qualification for a tool the colony genuinely registers.
            "colony_state",
            // v0.3.8.102 — the tester's operation lane. Adopted by the API host where the homelab
            // executor is built rather than by the Queen; "fully equipped" means that composition
            // happened, exactly as it means the tools module was loaded.
            Anthill.SDK.Contracts.SystemActionToolNames.Propose,
            Anthill.SDK.Contracts.SystemActionToolNames.Execute,
            // v0.3.8.103 — the tester's send lane, adopted at composition for the same reason.
            Anthill.SDK.Contracts.ExternalActionToolNames.Propose,
            Anthill.SDK.Contracts.ExternalActionToolNames.Execute,
            // v0.3.8.106 — the researcher's continuity lane, registered in the CORE beside
            // `colony_state`. Same reason it is here: the contract declares it, so a fixture that
            // omitted it would fail qualification for a tool the colony genuinely registers.
            Anthill.Core.Tools.ReadArtifactTool.ToolName,
        };

    /// <summary>
    /// THE GATE. Under `roster_profile: full`, every one of the twelve must have a handler-backed
    /// contract whose declared tools are registered and whose required capabilities this run can
    /// grant. A failure names the role and the reason.
    /// </summary>
    [Fact]
    public void AllTwelveRoles_QualifyUnderTheFullProfile()
    {
        var roster = RosterProfiles.Resolve(RosterProfiles.Full, disabledRoles: null, fromFlags:
            new RosterActivation(false, ActivationTier.Core, false, false, false, false, false, false, false, false));

        Assert.True(roster.SpecialistExecution, "the full profile must enable specialist execution");
        Assert.Equal(ActivationTier.Full, roster.Tier);

        var registered = FullyEquipped();
        var granted = CapabilityGrant.Resolve(registered, modelAvailable: true, webSearchEnabled: true);

        var problems = new List<string>();
        foreach (var role in TwelveRoles)
        {
            var contract = AntExecutionCatalog.ContractFor(role);
            if (contract is null) { problems.Add($"{role}: no execution contract"); continue; }

            if (contract.SupportedTaskTypes.Count == 0)
                problems.Add($"{role}: contract declares no supported task types");

            var missingTools = contract.AllowedTools.Where(t => !registered.Contains(t)).ToList();
            if (missingTools.Count > 0)
                problems.Add($"{role}: declared tools not registered — {string.Join(", ", missingTools)}");

            var ungranted = contract.RequiredCapabilities.Where(c => !granted.Contains(c)).ToList();
            if (ungranted.Count > 0)
                problems.Add($"{role}: required capabilities not grantable — {string.Join(", ", ungranted)}");

            // The prohibition that must survive every profile. `full` widens what runs; it must
            // never widen what a mission agent may do.
            foreach (var forbidden in new[] { "apply_patch", "write_text_file", "shell_command" })
                if (contract.AllowedTools.Contains(forbidden))
                    problems.Add($"{role}: the full profile must not grant {forbidden}");
        }

        Assert.True(problems.Count == 0,
            "The full roster does not qualify:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// A colony with NO model. Qualification must not silently depend on a provider being up.
    ///
    /// v3.8.32 — the test above hardcoded <c>modelAvailable: true</c>, which an external review
    /// correctly flagged: the fixture asserted the roster qualifies in a world it simply declared to
    /// exist. This runs the same resolution honestly and states what changes.
    ///
    /// The point is NOT that everything still qualifies — it does not, and should not. The point is
    /// that the difference is exactly the model capability, so an operator reading a qualification
    /// failure offline sees "no model" rather than a structural problem that is not there.
    /// </summary>
    [Fact]
    public void WithNoModelAvailable_OnlyTheModelCapabilityIsMissing()
    {
        var registered = FullyEquipped();
        var withModel = CapabilityGrant.Resolve(registered, modelAvailable: true, webSearchEnabled: true);
        var without = CapabilityGrant.Resolve(registered, modelAvailable: false, webSearchEnabled: true);

        var lost = withModel.Except(without).ToList();

        Assert.Equal(new[] { Capability.ModelInvoke }, lost);

        // And every role whose contract does NOT require a model still qualifies offline, which is
        // the claim "the colony functions without an LLM" reduces to at the contract layer.
        foreach (var role in TwelveRoles)
        {
            var contract = AntExecutionCatalog.ContractFor(role)!;
            if (contract.RequiredCapabilities.Contains(Capability.ModelInvoke)) continue;

            var ungranted = contract.RequiredCapabilities.Where(c => !without.Contains(c)).ToList();
            Assert.True(ungranted.Count == 0,
                $"{role} needs no model but still cannot be granted: {string.Join(", ", ungranted)}");
        }
    }

    /// <summary>
    /// Every role has a SCHEDULING TRIGGER, and the trigger is a REAL one.
    ///
    /// v3.8.32 — the previous version of this test checked
    /// <c>ContractFor(r)?.Scheduling is null</c>. <c>Scheduling</c> is a non-nullable enum, so that
    /// expression can only be null when the CONTRACT is missing: it was a duplicate
    /// contract-existence check wearing a trigger test's name, and it would have passed just as
    /// happily if every role in the colony were unreachable.
    ///
    /// A trigger is only real if something can pull it. So this asserts the mode is one the runtime
    /// actually implements, and that every non-planner mode has at least one role — a mode nothing
    /// uses is a mechanism nobody has tested.
    /// </summary>
    [Fact]
    public void EveryRole_HasARealTrigger()
    {
        var modes = TwelveRoles.ToDictionary(r => r, r => AntExecutionCatalog.ContractFor(r)!.Scheduling);

        // Every declared mode is one the runtime knows how to pull.
        Assert.All(modes, kv => Assert.True(Enum.IsDefined(kv.Value), $"{kv.Key}: unknown mode {kv.Value}"));

        // The three non-planner modes exist BECAUSE some role depends on them. An empty one means
        // either a role lost its trigger or a mechanism is dead code.
        foreach (var mode in new[]
                 {
                     SchedulingMode.PolicyInserted,
                     SchedulingMode.FailureTriggered,
                     SchedulingMode.PostFinalization,
                 })
            Assert.True(modes.Any(kv => kv.Value == mode),
                $"no role is scheduled by {mode} — the mechanism has no user, so nothing exercises it");

        // And the roles whose triggers this program was built to install are on the modes that
        // actually reach them. Pinned by name: these four are the ones that had never run.
        Assert.Equal(SchedulingMode.FailureTriggered, modes["medic"]);
        Assert.Equal(SchedulingMode.PostFinalization, modes["archivist"]);
        Assert.Equal(SchedulingMode.PolicyInserted, modes["tester"]);
        Assert.Equal(SchedulingMode.PolicyInserted, modes["soldier"]);
    }

    /// <summary>
    /// The roster this fixture qualifies is the WHOLE contracted roster, not a list that happens to
    /// have twelve entries in it.
    ///
    /// Without this, a role added to the catalog and forgotten here would be qualified by nothing
    /// while every test in the file still passed — the same shape as the archivist defect.
    /// </summary>
    [Fact]
    public void TheTwelveNamedRoles_AreExactlyTheContractedRoles()
    {
        Assert.Equal(
            AntExecutionCatalog.Contracts.Keys.OrderBy(r => r, StringComparer.Ordinal).ToArray(),
            TwelveRoles.OrderBy(r => r, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The tool list this fixture calls "fully equipped" must be a real superset of what the colony
    /// registers. A hand-written list that drifts below the contracts would make
    /// <see cref="AllTwelveRoles_QualifyUnderTheFullProfile"/> fail for the wrong reason, and one
    /// that drifts ABOVE them would qualify tools nothing provides.
    /// </summary>
    [Fact]
    public void TheFullyEquippedToolSet_CoversEveryDeclaredTool()
    {
        var declared = AntExecutionCatalog.Contracts.Values
            .SelectMany(c => c.AllowedTools)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var missing = declared.Where(t => !FullyEquipped().Contains(t)).ToList();

        Assert.True(missing.Count == 0,
            "the fixture claims a fully-equipped colony but omits tools roles declare: "
            + string.Join(", ", missing));
    }

    /// <summary>
    /// The default IS `full`, as of v0.3.8.41 — and this test inverting is the intended change.
    ///
    /// For six releases it asserted the opposite, with the reasoning that qualification proves the
    /// roster CAN run without deciding that it should. The staged rollout was always meant to end,
    /// and it ends here: all twelve roles are structurally qualified by the fixture in this file, and
    /// all twelve now have a production trigger — policy insertion for tester, soldier and verifier
    /// (v3.8.26 and v0.3.8.41), failure for the medic, post-finalization for the archivist, the plan
    /// for the rest. A role that is qualified and switched off by default is a role nobody runs.
    ///
    /// Stated plainly, because the distinction is exactly the kind this suite exists to keep honest:
    /// there is still NO deterministic Queen-driven acceptance test reaching all twelve through those
    /// triggers in one mission. What this file proves is that nothing STRUCTURAL stops the roster.
    /// The rest is recorded as open in <c>docs/PLAN.md</c>.
    ///
    /// The rollout discipline moves to <see cref="ConfigSchema"/> rather than disappearing. An
    /// existing installation is migrated ONLY if it never touched the roster; anything explicit
    /// survives, and <c>disabled_roles</c> survives unconditionally.
    /// </summary>
    [Fact]
    public void TheDefaultProfileIsFull_AndKillSwitchesStillStartEmpty()
    {
        var config = new AnthillConfig();

        Assert.Equal(RosterProfiles.Full, config.RosterProfile);
        Assert.Empty(config.DisabledRoles);
        Assert.Equal(ConfigSchema.Current, config.ConfigSchemaVersion);
    }

    /// <summary>
    /// The trail vocabulary keeps role reputation and environment reliability apart. A failing tool
    /// and a failing worker are different facts with different remedies, and they shared a column
    /// until v3.8.29.
    /// </summary>
    [Fact]
    public void ReputationAndEnvironmentTrails_AreDistinctKinds()
    {
        Assert.True(TrailKind.IsReputation(TrailKind.Ant));
        Assert.True(TrailKind.IsReputation(TrailKind.Worker));

        foreach (var environmental in new[]
                 { TrailKind.Tool, TrailKind.Capability, TrailKind.SourceDomain, TrailKind.ExternalResearchTool })
            Assert.False(TrailKind.IsReputation(environmental));

        // A pattern trail is about a SEQUENCE, not about any one worker in it.
        Assert.False(TrailKind.IsReputation(TrailKind.WorkerPattern));
    }

    /// <summary>
    /// An unseen subject is NEUTRAL and not established. "We have never seen this role work" and
    /// "this role works badly" are different facts — conflating them is exactly how a specialist
    /// enabled for the first time would be routed away from before it ran once.
    /// </summary>
    [Fact]
    public void AnUnseenRole_IsNeutralNotBad()
    {
        var reputation = Reputation.Unknown("medic");

        Assert.Equal(0.5, reputation.Strength);
        Assert.False(reputation.Established);
        Assert.Equal(0, reputation.Observations);
    }

    /// <summary>A reputation below the observation floor is reported but NOT established, so a
    /// caller cannot route on one lucky run.</summary>
    [Fact]
    public void AThinlyObservedReputation_IsNotEstablished()
    {
        var thin = Reputation.From("tester", strength: 0.9, successes: 1, failures: 0, minObservations: 3);
        var solid = Reputation.From("tester", strength: 0.9, successes: 3, failures: 0, minObservations: 3);

        Assert.False(thin.Established);
        Assert.True(solid.Established);
    }
}
