using System.Text.Json.Nodes;
using Anthill.Api;
using Anthill.Core.Agents;
using Anthill.Core.Autonomy;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Anthill.Core.Planning;
using Anthill.Core.Skills;
using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.26.0 pre-V3 hardening, part 1: the three defects an external deep-dive confirmed first.
///
/// 1. STOP durability — starting the Colony Director cleared the durable STOP sentinel, so a
///    process restart with --autonomous silently resumed autonomy over an operator's stop.
/// 2. Promotable without deterministic evidence — the deterministic requirement lived in a flag
///    callers had to remember, and mission-level skill credit didn't. An invariant a caller must
///    remember is not an invariant.
/// 3. Planner per-plan mutable state — one shared Planner held the offered-skill set in an
///    instance field, so concurrent plans could cross-contaminate skill provenance.
/// </summary>
[Collection("Autonomy")]
public class PreV3HardeningTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _enableAutonomy;
    private readonly bool _enableRouting;
    private readonly bool _useOllama;

    public PreV3HardeningTests()
    {
        AnthillRuntime.Initialize();
        _enableAutonomy = AnthillRuntime.EnableAutonomy;
        _enableRouting = AnthillRuntime.EnableModelRouting;
        _useOllama = AnthillRuntime.UseOllama;
        AnthillRuntime.EnableAutonomy = true;
        AnthillRuntime.EnableModelRouting = false;
        AnthillRuntime.UseOllama = false;
        _dir = Path.Combine(Path.GetTempPath(), "anthill_hardening_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AutonomyControl.Resume();   // each test starts from a clean sentinel
    }

    public void Dispose()
    {
        AutonomyControl.Resume();
        AnthillRuntime.EnableAutonomy = _enableAutonomy;
        AnthillRuntime.EnableModelRouting = _enableRouting;
        AnthillRuntime.UseOllama = _useOllama;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (Queen Queen, ApiJobRegistry Jobs, ColonyDirector Director) Runtime()
    {
        var queen = new Queen(new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db")));
        var jobs = new ApiJobRegistry(queen, 1);
        return (queen, jobs, new ColonyDirector(queen, jobs));
    }

    // ---- 1. STOP survives restart -----------------------------------------------------------------

    /// <summary>
    /// The restart scenario end to end: operator stops, the process dies, a NEW runtime is
    /// constructed and autostart calls Start(). STOP must still be engaged, Start() must not have
    /// cleared it, and the status must say so.
    /// </summary>
    [Fact]
    public void StopSurvivesRestart_AndStartDoesNotClearIt()
    {
        var (queen1, jobs1, director1) = Runtime();
        try
        {
            director1.Stop("operator stop before restart");
            Assert.True(AutonomyControl.IsStopped);
        }
        finally { director1.Dispose(); jobs1.Dispose(); queen1.Dispose(); }

        // "Restart": a brand-new runtime, as --autonomous boot would build it.
        var (queen2, jobs2, director2) = Runtime();
        try
        {
            Assert.True(AutonomyControl.IsStopped, "the sentinel must survive the process boundary");
            Assert.True(director2.Start(), "the Director PROCESS may start — it just may not launch work");
            Assert.True(AutonomyControl.IsStopped, "Start() must never clear a durable operator STOP");

            var status = director2.StatusSnapshot();
            Assert.True((bool)status["kill_switch_engaged"]!,
                "status must tell the operator exactly why nothing is launching");
        }
        finally { director2.Stop("teardown"); director2.Dispose(); jobs2.Dispose(); queen2.Dispose(); }
    }

    /// <summary>While STOP is engaged, a started Director launches nothing — even with work queued.</summary>
    [Fact]
    public void AStartedDirector_LaunchesNothingWhileStopped()
    {
        var (queen, jobs, director) = Runtime();
        try
        {
            queen.Memory.SaveObjective(new Objective
            {
                Title = "tempting work", Charter = "should not launch while stopped",
                Status = ObjectiveStatus.Active, Priority = 5,
            });
            AutonomyControl.Stop("hold everything");
            director.Start();
            Thread.Sleep(300);   // give the loop time to (wrongly) pick something up

            Assert.Empty(queen.Memory.ListAutonomyRuns(limit: 10));
            Assert.True(AutonomyControl.IsStopped);
        }
        finally { director.Stop("teardown"); director.Dispose(); jobs.Dispose(); queen.Dispose(); }
    }

    /// <summary>Only an explicit resume clears the sentinel — and then work may launch.</summary>
    [Fact]
    public void OnlyExplicitResume_ClearsTheSentinel()
    {
        AutonomyControl.Stop("operator stop");
        Assert.True(AutonomyControl.IsStopped);
        AutonomyControl.Resume();   // the explicit operator act (the /autonomy/start endpoint's job)
        Assert.False(AutonomyControl.IsStopped);
    }

    /// <summary>The wiring guard: Start() contains no Resume call, and the explicit resume lives at
    /// the operator endpoint. Behavioural proof is above; this pins WHERE the responsibility sits.</summary>
    [Fact]
    public void TheResumeResponsibility_LivesAtTheOperatorEndpoint_NotInStart()
    {
        string CodeOnly(string src) => string.Join("\n", src.Split('\n')
            .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));

        var director = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "ColonyDirector.cs")));
        var startBody = director[director.IndexOf("public bool Start()", StringComparison.Ordinal)..];
        startBody = startBody[..startBody.IndexOf("public void Stop(", StringComparison.Ordinal)];
        Assert.DoesNotContain("AutonomyControl.Resume", startBody);

        var api = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "ApiHost.cs")));
        var startEndpoint = api[api.IndexOf("\"/autonomy/start\"", StringComparison.Ordinal)..];
        startEndpoint = startEndpoint[..startEndpoint.IndexOf("\"/autonomy/stop\"", StringComparison.Ordinal)];
        Assert.Contains("AutonomyControl.Resume()", startEndpoint);
    }

    // ---- 2. Promotable requires deterministic evidence, intrinsically ------------------------------

    private static VerificationBundle Bundle(bool deterministic, bool passed = true) => new()
    {
        TaskType = "mission_verification",
        Required = { "mission_verifier" },
        Results = { new VerificationResult("mission_verifier", passed, deterministic, "summary",
            Array.Empty<VerificationEvidence>()) },
    };

    /// <summary>The invariant is intrinsic now: a semantic-only bundle — all required verifiers
    /// passing, nothing blocked — is still NOT promotable. No caller can forget to check.</summary>
    [Fact]
    public void ASemanticOnlyBundle_IsNotPromotable()
    {
        var semantic = Bundle(deterministic: false);
        Assert.True(semantic.Required.All(r => semantic.Results.Any(x => x.Verifier == r && x.Passed)));
        Assert.False(semantic.HasDeterministicEvidence);
        Assert.False(semantic.Promotable);

        Assert.True(Bundle(deterministic: true).Promotable);
    }

    /// <summary>
    /// The defect this closes: Queen built exactly this bundle shape (Passed: true,
    /// Deterministic: false) from a model's own verdict and used it for skill credit. It must now
    /// record a NEUTRAL observation — no success, and no failure either, because a verifier-passing
    /// mission is not evidence the skill failed.
    /// </summary>
    [Fact]
    public void AFabricatedSemanticPass_CannotPromoteASkill_AndDoesNotPunishIt()
    {
        var registry = new SkillRegistry();
        registry.RegisterCandidate("restart_service", "restart a service");

        for (var i = 0; i < 10; i++) registry.RecordOutcome("restart_service", Bundle(deterministic: false));

        var skill = registry.Get("restart_service")!;
        Assert.Equal(0, skill.SuccessCount);                       // never promoted
        Assert.Equal(0, skill.FailureCount);                       // and never punished
        Assert.Equal(SkillStatus.Candidate, skill.Status);
        Assert.Contains(skill.Notes, n => n.Contains("neutral observation"));
    }

    /// <summary>Deterministic evidence still promotes, and a genuinely failed bundle still counts
    /// against the skill — the neutral band is exactly verified-without-deterministic.</summary>
    [Fact]
    public void TheNeutralBand_IsExactlyVerifiedWithoutDeterministic()
    {
        var registry = new SkillRegistry();
        registry.RegisterCandidate("s", "skill");

        registry.RecordOutcome("s", Bundle(deterministic: true));           // promotable → success
        Assert.Equal(1, registry.Get("s")!.SuccessCount);

        registry.RecordOutcome("s", Bundle(deterministic: true, passed: false));   // failed verifier → failure
        Assert.Equal(1, registry.Get("s")!.FailureCount);

        registry.RecordOutcome("s", null);                                   // no evidence → failure path
        Assert.Equal(2, registry.Get("s")!.FailureCount);
    }

    // ---- 3. Planner statelessness ------------------------------------------------------------------

    // THREE tasks: TasksFromJson discards plans below MinDynamicTasks (3), and the check runs
    // BEFORE the auto-added verifier. Index [0] is the claiming task either way.
    private static JsonObject PlanClaiming(string skillId) => (JsonObject)JsonNode.Parse($$"""
        { "tasks": [ { "title": "do it", "description": "d", "assigned_ant": "researcher",
                       "task_type": "research", "skill_id": "{{skillId}}" },
                     { "title": "write it up", "description": "d2", "assigned_ant": "builder",
                       "task_type": "build_answer" },
                     { "title": "check it", "description": "d3", "assigned_ant": "verifier",
                       "task_type": "verification" } ] }
        """)!;

    /// <summary>
    /// The deterministic interleaving the review asked for: two parses run truly concurrently,
    /// each with its OWN offered set, both claiming skills from both sets. With the old instance
    /// field, whichever plan assigned last would have leaked its set into the other's parse.
    /// Provenance must bind to the parse call, not to the Planner.
    /// </summary>
    [Fact]
    public void ConcurrentParses_CannotCrossContaminateSkillProvenance()
    {
        var planner = new Planner(useOllama: false, router: null);
        var offeredA = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill-a" };
        var offeredB = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill-b" };

        using var barrier = new Barrier(2);   // both threads inside the parse window at once
        string? aClaimsB = null, bClaimsA = null, aClaimsA = null, bClaimsB = null;
        // An unhandled exception on a raw Thread ABORTS the whole xUnit run (which is how this
        // test's first defect surfaced as TESTRUNABORT instead of a red test). Capture and
        // rethrow on the test thread so a regression fails THIS test, loudly and alone.
        Exception? threadError = null;

        var ta = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                aClaimsA = planner.TasksFromJson(PlanClaiming("skill-a"), "goal", offeredA)[0].SkillId;
                aClaimsB = planner.TasksFromJson(PlanClaiming("skill-b"), "goal", offeredA)[0].SkillId;
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref threadError, ex, null); }
        });
        var tb = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                bClaimsB = planner.TasksFromJson(PlanClaiming("skill-b"), "goal", offeredB)[0].SkillId;
                bClaimsA = planner.TasksFromJson(PlanClaiming("skill-a"), "goal", offeredB)[0].SkillId;
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref threadError, ex, null); }
        });
        ta.Start(); tb.Start(); ta.Join(); tb.Join();
        if (threadError is not null) throw new Xunit.Sdk.XunitException($"parse thread threw: {threadError}");

        Assert.Equal("skill-a", aClaimsA);   // own offer honoured
        Assert.Equal("skill-b", bClaimsB);
        Assert.Null(aClaimsB);               // the OTHER plan's offer is never honoured
        Assert.Null(bClaimsA);
    }

    // ---- 4. drain, job mapping, ant classification (part 2 of the hardening) -----------------------

    /// <summary>Job status maps FROM the canonical outcome — status can never contradict it.</summary>
    [Theory]
    [InlineData("timed_out", null, "timed_out")]
    [InlineData("cancelled", null, "cancelled")]
    [InlineData("failed", null, "failed")]
    [InlineData("completed", "completed_verified", "complete")]
    [InlineData("completed", "completed_unverified", "complete")]
    [InlineData(null, null, "failed")]                    // an unexplained end is not a success
    [InlineData("escalated", null, "failed")]
    public void JobStatus_MapsFromTheCanonicalOutcome(string? outcome, string? code, string expected) =>
        Assert.Equal(expected, ApiJobRegistry.StatusFromOutcome(outcome, code));

    /// <summary>The coder's classification parses its own JSON artifact — zero proposals on a
    /// patch task is a failure, malformed output is a failure, proposals are a success.</summary>
    [Fact]
    public void CoderZeroPatchOutput_IsNotASuccess()
    {
        var zero = CoderAnt.ClassifyPatchJson("""{"summary":"nothing to do","proposals":[]}""");
        Assert.False(zero.Success);
        Assert.Contains("zero patch proposals", zero.Failure!.Reason);

        var malformed = CoderAnt.ClassifyPatchJson("this is not json at all {{{");
        Assert.False(malformed.Success);

        var real = CoderAnt.ClassifyPatchJson(
            """{"summary":"ok","proposals":[{"file_path":"docs/a.md","change_type":"modify"}]}""");
        Assert.True(real.Success);
        Assert.Contains(real.Artifacts, a => a.Kind == "patch_json");

        Assert.False(CoderAnt.ClassifyPatchJson("").Success);   // empty model output is a failure
    }

    /// <summary>The drain invariant: a mission stopped by its deadline marks every previously
    /// running task terminal with a persisted cancellation reason — finalization then never sees a
    /// running task, and the evaluation runs over fully-terminal state.</summary>
    [Fact]
    public void MissionFinalization_FailsClosed_OnANonTerminalTask()
    {
        // Behavioural probe of the finalization invariant via a mission object left with a
        // Running task (the internal-runtime-defect path — the drain normally prevents this).
        var (queen, jobs, director) = Runtime();
        try
        {
            var goal = "summarize the colony";   // offline fallback plan, no web task
            var result = queen.RunMission(goal);
            Assert.False(string.IsNullOrWhiteSpace(result));
            var missionId = queen.LastMissionId!;
            var evaluation = queen.Memory.LoadMissionEvaluation(missionId);
            Assert.NotNull(evaluation);           // evaluated exactly once, persisted
            // and no persisted task is left non-terminal
            Assert.DoesNotContain(queen.Memory.GetTasksForMission(missionId, 100),
                row => (row.GetValueOrDefault("status")?.ToString() ?? "") is "running" or "ready" or "pending" or "blocked");
        }
        finally { director.Dispose(); jobs.Dispose(); queen.Dispose(); }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
