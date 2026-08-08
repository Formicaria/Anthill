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
    /// Every role has a SCHEDULING TRIGGER. A qualified role nothing can reach is the defect this
    /// whole program was built to find — the archivist had a handler, a contract and a gate for
    /// releases and had never once run.
    /// </summary>
    [Fact]
    public void EveryRole_HasARealTrigger()
    {
        var untriggered = TwelveRoles
            .Select(r => (Role: r, Mode: AntExecutionCatalog.ContractFor(r)?.Scheduling))
            .Where(x => x.Mode is null)
            .Select(x => x.Role)
            .ToList();

        Assert.True(untriggered.Count == 0,
            "These roles declare no scheduling mode, so nothing states how they are reached: "
            + string.Join(", ", untriggered));
    }

    /// <summary>
    /// The default is STILL `core`. Qualification proves the roster CAN run; it does not decide that
    /// it should, and a release that silently switched six roles on for every existing installation
    /// would invert the rollout discipline the whole program rests on.
    /// </summary>
    [Fact]
    public void QualifyingDoesNotChangeTheDefault()
    {
        var config = new AnthillConfig();

        Assert.Equal(RosterProfiles.Core, config.RosterProfile);
        Assert.Empty(config.DisabledRoles);
        Assert.False(config.SpecialistAntExecutionEnabled);
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
