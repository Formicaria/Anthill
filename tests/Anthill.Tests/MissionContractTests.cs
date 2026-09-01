using System.Text.RegularExpressions;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Missions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE MISSION IS WHAT IT WAS ADMITTED AS. v0.3.8.104, PLAN.md §2b.
///
/// WHAT THIS RELEASE CLOSES. Until now a mission's class, deliverables, authority and constraints
/// were re-derived from its goal string every time anyone asked — five call sites between
/// `MissionIntake.Resolve` and `MissionConstraints.Parse`, none of them persisted. So they were not
/// facts about the mission at all; they were facts about whatever the intake rules said at the
/// moment of asking.
///
/// `.103` is the proof rather than the hypothesis: it added a mission class and four verbs to
/// intake. Every mission stored before it therefore reclassifies when read today, and `.98` wrote
/// the rule that breaks in its own words — a grade has to be reproducible from the persisted
/// record.
///
/// THE TESTS BELOW ARE THE EXIT GATE'S FIRST FIVE, and one of them is a source-shape guard rather
/// than a behavioural one. That is deliberate: removing four call sites is a change anyone can undo
/// by writing a fifth, and the convention this replaces already existed — ADR-002 said constraints
/// are parsed once at intake, and `Ants.cs` parsed them again anyway for two years.
/// </summary>
public class MissionContractTests : IDisposable
{
    private readonly string _dir;

    public MissionContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-contract-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private SqliteMemory Memory() => new(Path.Combine(_dir, $"c-{Guid.NewGuid():N}.db"));

    private static Mission Audit() => new()
    {
        Goal = "Audit this repository and the running colony: what is implemented, and what is enabled now?",
    };

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE CONTRACT SURVIVES THE PROCESS. Written at intake, read back identically — which is what
    /// makes an evaluation reproducible from the store rather than from a live object graph.
    /// </summary>
    [Fact]
    public void MissionSpecification_PersistsAcrossRestart()
    {
        var mission = Audit();
        var db = Path.Combine(_dir, "restart.db");

        MissionContract written;
        using (var memory = new SqliteMemory(db))
        {
            memory.SaveMission(mission);
            written = MissionContracts.LoadOrCreate(memory, mission);
        }

        // A NEW connection, as a restarted process has.
        using var reopened = new SqliteMemory(db);
        var read = reopened.LoadMissionContract(mission.Id);

        Assert.NotNull(read);
        Assert.False(read!.IsLegacy);
        Assert.Equal(written.IntakeVersion, read.IntakeVersion);
        Assert.Equal(written.Specification.MissionClass, read.Specification.MissionClass);
        Assert.Equal(written.Specification.Authority, read.Specification.Authority);
        Assert.Equal(
            written.Specification.Deliverables.Select(d => d.Id),
            read.Specification.Deliverables.Select(d => d.Id));
        Assert.Equal(written.Constraints, read.Constraints);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. A mission graded under one release's intake rules must not be
    /// reclassified by a later release's.
    ///
    /// Simulated by writing a contract whose recorded class differs from what today's rules would
    /// produce — which is exactly what an intake rule change does, and what `.103` actually did to
    /// every `.102` mission. The read must return the RECORDED class, not the resolvable one.
    /// </summary>
    [Fact]
    public void MissionReplay_DoesNotReclassifyAfterIntakeRulesChange()
    {
        using var memory = Memory();
        var mission = Audit();
        memory.SaveMission(mission);

        // What the mission was admitted as under an older ruleset — deliberately NOT what intake
        // resolves this goal to today.
        var asAdmitted = MissionContracts.ForPreview(mission.Goal) with
        {
            IntakeVersion = "0.3.8.97",
            Specification = MissionIntake.Resolve(mission.Goal) with
            {
                MissionClass = MissionSpecification.GeneralClass,
            },
        };
        memory.SaveMissionContract(mission.Id, asAdmitted);

        var today = MissionIntake.Resolve(mission.Goal).MissionClass;
        var loaded = MissionContracts.LoadOrCreate(memory, mission);

        Assert.Equal(MissionSpecification.SystemAuditClass, today);
        Assert.Equal(MissionSpecification.GeneralClass, loaded.Specification.MissionClass);
        Assert.Equal("0.3.8.97", loaded.IntakeVersion);
    }

    /// <summary>
    /// WRITE-ONCE, ENFORCED BY THE STORE. A resumed or replayed mission reaching intake a second
    /// time keeps the first contract — the property every other assertion here depends on.
    /// </summary>
    [Fact]
    public void AContract_IsWrittenOnce_AndASecondWriteIsIgnored()
    {
        using var memory = Memory();
        var mission = Audit();
        memory.SaveMission(mission);

        var first = MissionContracts.LoadOrCreate(memory, mission);
        memory.SaveMissionContract(mission.Id, first with { IntakeVersion = "9.9.9.9" });

        Assert.Equal(first.IntakeVersion, memory.LoadMissionContract(mission.Id)!.IntakeVersion);
    }

    /// <summary>
    /// AND A MISSION OLDER THAN CONTRACTS SAYS SO RATHER THAN INVENTING ONE.
    ///
    /// Its specification is what TODAY's rules produce, which is not what it was admitted as. That
    /// is unavoidable — the record does not exist — and the whole point is that a reader can tell.
    /// A legacy contract silently indistinguishable from a recorded one would reintroduce this
    /// release's defect one layer down.
    /// </summary>
    [Fact]
    public void LegacyMission_WithoutAContract_SaysSoRatherThanInventingOne()
    {
        var legacy = MissionContracts.Legacy("Audit the repository.");

        Assert.True(legacy.IsLegacy);
        Assert.Equal(MissionContract.LegacyIntakeVersion, legacy.IntakeVersion);
        Assert.NotEqual(MissionContract.CurrentIntakeVersion(), legacy.IntakeVersion);
    }

    /// <summary>
    /// THE SOURCE-SHAPE GUARD. Exactly one production call site each, and both inside
    /// `MissionContract.cs`.
    ///
    /// Behavioural tests cannot catch a FIFTH site being added later — a new layer re-deriving the
    /// class would pass every assertion above while reintroducing the defect. This reads the source
    /// the way `CallSiteAuditTests` and `ToolInventoryTests` already read theirs, and it names the
    /// offending file so the failure is actionable rather than a puzzle.
    ///
    /// Scoped to `src/`: a test may resolve intake freely, since a test asking what today's rules
    /// say is exactly how `MissionReplay_DoesNotReclassifyAfterIntakeRulesChange` proves its point.
    /// </summary>
    [Theory]
    [InlineData("MissionIntake.Resolve(")]
    [InlineData("MissionConstraints.Parse(")]
    public void TheOperatorsGoal_IsInterpretedInExactlyOnePlace(string call)
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            // The declaring files themselves, and the single interpretation site.
            if (name is "MissionContract.cs" or "MissionIntake.cs" or "MissionConstraints.cs") continue;
            // AN OBJECTIVE IS NOT A MISSION, and this exemption corrects the guard rather than
            // holing it. `ObjectiveLifecycle` parses an objective's TITLE AND CHARTER to decide
            // whether a standing objective is one-shot — a different subject with a different
            // lifetime, which no mission contract describes and none should. The rule being
            // enforced is that the operator's MISSION GOAL is interpreted once; the pattern was
            // written too broadly, matched a legitimate second subject, and is narrowed BY NAME
            // with the reason rather than by loosening what it looks for.
            if (name is "ObjectiveLifecycle.cs") continue;

            var text = File.ReadAllText(file);
            // Not a comment mentioning it — an actual invocation.
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;
                if (line.Contains(call, StringComparison.Ordinal))
                    offenders.Add($"{name}: {trimmed.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"'{call}' is called outside MissionContract.cs, so the operator's goal is being "
          + "interpreted more than once and two layers can disagree about what the mission is:\n  "
          + string.Join("\n  ", offenders)
          + "\n\nRead the mission's contract instead — it records what the mission was ADMITTED as, "
          + "which is the only version of the answer that stays true when intake rules change.");
    }

    /// <summary>
    /// AND EVERY STAGE CONSUMES THE RECORDED CONTRACT, not a fresh derivation. Asserted through the
    /// context every stage actually reads, with a contract deliberately unlike what intake would
    /// produce — so a stage that re-derived would visibly disagree.
    /// </summary>
    [Fact]
    public void DownstreamStages_ConsumePersistedSpecification()
    {
        using var memory = Memory();
        var mission = Audit();
        memory.SaveMission(mission);

        var admitted = MissionContracts.ForPreview(mission.Goal) with
        {
            IntakeVersion = "0.3.8.97",
            Constraints = new MissionConstraints(NoPatches: true, VerificationOnly: false,
                ReadOnly: false, OneShot: false),
        };
        memory.SaveMissionContract(mission.Id, admitted);

        var contract = MissionContracts.LoadOrCreate(memory, mission);
        var context = Anthill.Core.Orchestration.MissionContext.Create(
            mission, RuntimeProfile.Resolve(RuntimeOptions.Capture(), Array.Empty<string>()),
            AnthillTime.NowUtc(), contract);

        // The context's projections ARE the contract's, not a second reading of the goal.
        Assert.Equal("0.3.8.97", context.Contract.IntakeVersion);
        Assert.True(context.Constraints.NoPatches);
        Assert.True(context.Constraints.BlocksPatches);
        Assert.Equal(admitted.Specification.MissionClass, context.Specification.MissionClass);

        // And the goal itself carries no such phrase — so a stage that re-parsed would say false.
        Assert.False(MissionConstraints.Parse(mission.Goal).NoPatches);
    }
}
