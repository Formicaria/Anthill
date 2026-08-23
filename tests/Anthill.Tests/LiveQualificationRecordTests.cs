using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Readiness;
using Anthill.Core.Security;   // WorkspacePathGuard, which the ToolsModule needs
using Anthill.Modules.Tools;
using Anthill.SDK.Events;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The live qualification recorder, proved against a scripted mission BEFORE any live run. v0.3.8.89.
///
/// WHY BUILD THE RECORDER FIRST. R4's exit gate is "a recorded run per provider with complete
/// telemetry", and `QUALIFICATION.md` §3 names the seven fields that run must capture. Almost all of
/// them are already persisted — provenance carries the provider and model that actually served each
/// call, `ModelRouter` logs tokens and durations, failure classes are typed, the consumption ledger
/// records what each role really read, and `MissionReconstruction` replays from artifact ids.
///
/// So the recorder is an ASSEMBLER over records the colony already keeps, which means every field can
/// be proved present and correct with no provider attached. Doing that first turns the live run into
/// an operator pressing go, instead of a live run plus an argument about whether its telemetry was
/// complete — and it removes the failure mode R4 is most exposed to, which is discovering a hole
/// mid-run and being unable to say whether the hole is in the provider or in the report.
///
/// WHAT A SCRIPTED RUN CANNOT PROVE, and this file says so rather than arranging otherwise: the
/// scripted provider returns `ModelResponse { Status = Ok, Content = answer }` and reports no usage,
/// so tokens stay UNMEASURED here. That is the correct outcome — a provider that reports nothing is
/// unknown, not zero — and the producer link is pinned separately by
/// <see cref="TheRecordersKeys_AreTheOnesTheRouterActuallyWrites"/>, with real adapters reading usage
/// already guarded by `AdapterConformanceTests` and `ProviderWireFormatTests`.
/// </summary>
[Collection("specialist-gates")]   // drives a real mission; workspace root and UseOllama are static
public class LiveQualificationRecordTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _useOllamaWas;
    private readonly string _workspaceRootWas;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public LiveQualificationRecordTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-liveqr-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path.Combine(_dir, "workspace"));
        File.WriteAllText(Path.Combine(_dir, "workspace", "README.txt"), "seed\n");

        _useOllamaWas = AnthillRuntime.UseOllama;
        _workspaceRootWas = AnthillRuntime.AllowedWorkspaceRoot;
    }

    // -----------------------------------------------------------------------------------------------
    // The scripted mission's two scripts, DECLARED AT CLASS LEVEL and not inside the method that
    // uses them. That placement is load-bearing, not style: `ScriptedPlanConformanceTests` resolves
    // `.Role("planner", NAME)` by finding `const string NAME = """…""";` in the same file, and a plan
    // held in a local `var` resolves to nothing. Its own words for what that costs — "a plan nothing
    // checks is a plan the Planner may have replaced" — describe the v0.3.8.82 defect exactly: the
    // Planner rejects a plan below `MinDynamicTasks`, substitutes `FallbackTasks`, and a fixture
    // asserting on a role the fallback happens to contain passes over a plan nobody wrote.
    //
    // The alternative the guard also accepts — verifying the plan at RUNTIME with
    // `AssertTheMissionRanTheScriptedPlan` — was considered and rejected here. This mission acquires
    // policy-inserted tester and soldier tasks as it runs, so "what the mission planned" is a larger
    // set than "what this file wrote", and this file's subject is the RECORD, not the plan. The
    // static form says everything this fixture needs said: three tasks, three planner-eligible roles.
    // -----------------------------------------------------------------------------------------------

    private const string ScriptedPlan = """
        {
          "tasks": [
            { "title": "Understand the request", "description": "Frame the note.",
              "assigned_ant": "researcher", "task_type": "research", "depends_on": [] },
            { "title": "Propose the documentation patch", "description": "Propose the note as JSON.",
              "assigned_ant": "coder", "task_type": "patch_proposal", "depends_on": [] },
            { "title": "Verify the outcome", "description": "Check the proposal.",
              "assigned_ant": "verifier", "task_type": "verification", "depends_on": [] }
          ]
        }
        """;

    private const string ScriptedProposals = """
        {
          "summary": "Add the requested colony note.",
          "proposals": [
            { "file_path": "docs/COLONY-NOTE.md", "change_type": "add", "old_content": null,
              "new_content": "# Colony note\n\nWritten through the real lifecycle.\n",
              "reason": "The mission asks for a documentation note.", "risk": "low" }
          ]
        }
        """;

    public void Dispose()
    {
        AnthillRuntime.UseOllama = _useOllamaWas;
        AnthillRuntime.AllowedWorkspaceRoot = _workspaceRootWas;
        RosterGates.Restore(_gatesWere);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------------------------------
    // The document is the specification
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// EVERY FIELD THE DOCUMENT DEMANDS HAS A PRODUCER, AND EVERY PRODUCER ANSWERS A ROW.
    ///
    /// `QUALIFICATION.md` §3's "What a run must record" table is the specification for a live run. A
    /// recorder whose fields drifted from it would produce a report that looks complete and answers a
    /// different question — the adjacent-question defect, applied to an exit gate.
    ///
    /// Both directions, because they fail differently: a row with no producer is a gate nothing can
    /// satisfy, and a producer with no row is a field nobody asked for that will be read as evidence.
    /// </summary>
    [Fact]
    public void EveryFieldTheDocumentRequires_HasAProducer_AndViceVersa()
    {
        var rows = DocumentRows();

        Assert.True(rows.Count >= 5,
            $"only {rows.Count} row(s) were parsed out of QUALIFICATION.md's 'What a run must record' "
          + "table. The table has more than that, so this guard is reading the wrong thing and would "
          + "pass over nothing.");

        var declared = QualificationFields.DocumentRows;

        var unproduced = rows.Where(r => !declared.Values.Contains(r, StringComparer.OrdinalIgnoreCase))
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        Assert.True(unproduced.Count == 0,
            "QUALIFICATION.md requires these fields and nothing produces them:\n  "
          + string.Join("\n  ", unproduced)
          + "\nAdd the field to QualificationFields with a real source, or take the row out of the "
          + "document — a row the recorder cannot answer is an exit gate nothing can satisfy.");

        var unasked = declared.Where(kv => !rows.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
            .Select(kv => $"{kv.Key} -> \"{kv.Value}\"")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(unasked.Count == 0,
            "these fields are produced and the document's table does not ask for them:\n  "
          + string.Join("\n  ", unasked)
          + "\nEither the row was reworded — in which case the recorder is now answering a question "
          + "nobody asked — or the field should be added to the table.");
    }

    /// <summary>The leading cell of every row in §3's "What a run must record" table.</summary>
    private static List<string> DocumentRows()
    {
        var doc = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "docs", "QUALIFICATION.md"));

        var start = doc.IndexOf("### What a run must record", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "QUALIFICATION.md no longer has a 'What a run must record' heading; the recorder's "
          + "specification has moved and this guard cannot find it.");

        var end = doc.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? doc[start..] : doc[start..end];

        return section.Split('\n')
            .Where(l => l.StartsWith("| ", StringComparison.Ordinal))
            .Select(l => l.Split('|', StringSplitOptions.None))
            .Where(cells => cells.Length >= 3)
            .Select(cells => cells[1].Trim())
            .Where(cell => cell.Length > 0
                        && !cell.Equals("Field", StringComparison.OrdinalIgnoreCase)
                        && !cell.All(c => c is '-' or ':' or ' '))
            .ToList();
    }

    // -----------------------------------------------------------------------------------------------
    // Against a real run
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A REAL MISSION PRODUCES A RECORD WITH REAL VALUES IN IT.
    ///
    /// Non-vacuity first and by construction: every assertion in this file would be satisfied by a
    /// recorder that returned an empty record for every mission, which is exactly how a reporting
    /// surface comes to certify nothing. So this drives a scripted mission through the Queen's public
    /// path and requires the record to describe it.
    /// </summary>
    [Fact]
    public void AScriptedMission_ProducesARecordThatDescribesIt()
    {
        var (memory, missionId) = RunScriptedMission();
        var record = LiveQualificationRecord.For(memory, memory, memory, missionId);

        Assert.Equal(missionId, record.MissionId);

        Assert.True(record.Roles.Count > 0,
            "the record names no roles for a mission that ran several. An empty record satisfies "
          + "every other assertion here, which is why this one comes first.");

        Assert.All(record.Roles, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Role));
            Assert.False(string.IsNullOrWhiteSpace(r.Trigger),
                $"'{r.Role}' has no recorded trigger. The document asks for this specifically, to "
              + "prove production triggers rather than that a harness called the role.");
        });

        // Every field carries a note whether or not it was measured — an unmeasured field with no
        // reason is indistinguishable from one nobody thought about.
        Assert.All(record.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Note)));

        // The provider that actually served the calls is recorded, even though it is a scripted one.
        var provider = record.Fields.Single(f => f.Field == QualificationFields.ProviderAndModel);
        Assert.True(provider.Measured,
            "no provider was recorded for a mission whose roles made model calls. The model_call "
          + "event carries the model that actually served the call; if that stopped being written, "
          + "a live run would attribute its results to nothing.");
        Assert.Contains(ScriptedColony.ProviderId, provider.Value ?? "");

        var wall = record.Fields.Single(f => f.Field == QualificationFields.WallTime);
        Assert.True(wall.Measured, "the mission produced no usable wall time from its task timestamps.");
    }

    /// <summary>
    /// A PROVIDER THAT REPORTS NOTHING LEAVES TOKENS UNKNOWN — never zero.
    ///
    /// The scripted provider returns a bare `Ok` response and reports no usage, which is faithful:
    /// several real providers do the same. The record must say so. Summing absent values to zero
    /// would turn "this provider does not report usage" into "this run used no tokens", and the
    /// second is a claim about the run that an operator would reasonably act on.
    /// </summary>
    [Fact]
    public void AProviderThatReportsNoUsage_LeavesTokensUnmeasured_NotZero()
    {
        var (memory, missionId) = RunScriptedMission();
        var record = LiveQualificationRecord.For(memory, memory, memory, missionId);

        var tokens = record.Fields.Single(f => f.Field == QualificationFields.Tokens);
        Assert.False(tokens.Measured,
            "tokens are reported as measured for a run whose provider reported none. Either the "
          + "scripted provider started reporting usage — in which case this guard should assert the "
          + "sum instead — or absent usage is being counted as zero.");
        Assert.Null(tokens.Value);

        Assert.All(record.Roles, r =>
        {
            Assert.Null(r.PromptTokens);
            Assert.Null(r.CompletionTokens);
        });

        Assert.Contains(tokens, record.Unmeasured);
    }

    /// <summary>
    /// COST IS RECORDED AS UNMEASURED, ALWAYS, AND NEVER COMPUTED.
    ///
    /// The document's table asks for cost "in the operator's currency". `ModelRouter` records tokens
    /// and nothing anywhere records money: converting one to the other needs a per-provider price
    /// table that does not exist as configuration. A rate assumed inside the recorder would put a
    /// figure in front of an operator that no part of this system can stand behind — so the gap is
    /// stated, and R4's exit gate cannot be read as met on this field.
    /// </summary>
    [Fact]
    public void Cost_IsAlwaysRecordedAsAGap_NeverAsANumber()
    {
        var (memory, missionId) = RunScriptedMission();
        var record = LiveQualificationRecord.For(memory, memory, memory, missionId);

        var cost = record.Fields.Single(f => f.Field == QualificationFields.Cost);

        Assert.False(cost.Measured,
            "cost is reported as measured. Nothing in this runtime converts tokens to currency; if "
          + "that changed, the price table is now a real input and this guard should assert the "
          + "conversion rather than the gap.");
        Assert.Null(cost.Value);
        Assert.Contains("price table", cost.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(cost, record.Unmeasured);
    }

    /// <summary>
    /// THE TRIGGER IS READ FROM THE ADMISSION EVENT, not inferred from the plan.
    ///
    /// This is the distinction the document is asking for: a role that appears in a task graph proves
    /// the plan wanted it, and a role named by an admission event proves the runtime produced it. The
    /// scripted mission's tester is POLICY-INSERTED — no script and no plan names it — so its trigger
    /// is the one that cannot be explained by the plan.
    /// </summary>
    [Fact]
    public void TheTrigger_IsReadFromTheAdmissionEvent_NotFromThePlan()
    {
        var (memory, missionId) = RunScriptedMission();
        var record = LiveQualificationRecord.For(memory, memory, memory, missionId);

        var tester = record.Roles.FirstOrDefault(r =>
            r.Role.Equals("tester", StringComparison.OrdinalIgnoreCase));

        Assert.True(tester is not null,
            "the tester never ran, so this mission does not exercise a policy insertion and the "
          + "trigger field is being proved by planner-assigned roles alone."
          + " Roles recorded: " + string.Join(", ", record.Roles.Select(r => r.Role)));

        Assert.Equal("policy_inserted", tester!.Trigger);

        // And the planner-assigned roles say so, rather than everything collapsing to one label.
        Assert.Contains(record.Roles, r => r.Trigger == "planned");
    }

    /// <summary>
    /// THE RECORDER READS THE KEYS THE ROUTER ACTUALLY WRITES.
    ///
    /// The real-producer link that a scripted run cannot supply. The recorder sums `prompt_tokens`,
    /// `completion_tokens` and `duration_ms` out of `model_call` metadata; `ModelRouter` is what
    /// writes them. A rename on either side would leave every test above green — the record would
    /// simply report everything as unmeasured, which is the failure mode this whole file exists to
    /// prevent.
    ///
    /// Read from the router's SOURCE rather than from a fixture event, because a fixture event would
    /// be the test writing both halves.
    /// </summary>
    [Fact]
    public void TheRecordersKeys_AreTheOnesTheRouterActuallyWrites()
    {
        var router = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Models", "ModelRouter.cs")));

        Assert.Contains("\"model_call\"", router, StringComparison.Ordinal);

        foreach (var key in new[] { "prompt_tokens", "completion_tokens", "duration_ms", "provider", "model" })
            Assert.True(router.Contains($"[\"{key}\"]", StringComparison.Ordinal),
                $"ModelRouter no longer writes '{key}' into the model_call event. The live "
              + "qualification record reads that key, so a live run would report this field as "
              + "unmeasured while the provider was reporting it perfectly well.");
    }

    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// One scripted patch mission: researcher → coder → verifier, with the review roles policy
    /// inserted off the patch set. The same spine `CodePatchLifecycleTests` drives, kept here so this
    /// file's subject is the RECORD rather than the lifecycle.
    /// </summary>
    private (SqliteMemory Memory, string MissionId) RunScriptedMission()
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableSoldierAnt = true;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.AllowedWorkspaceRoot = Path.Combine(_dir, "workspace");

        var book = new ScriptBook()
            .Role("planner", ScriptedPlan)
            .Role("researcher", "SCRIPTED: the note should describe the colony.")
            .Role("coder", ScriptedProposals)
            .Role("verifier", "SCRIPTED: the proposal addresses the request.")
            .Role("tester", "SCRIPTED: checks recorded.")
            .Role("soldier", "SCRIPTED: no security concern.")
            .Role("builder", "SCRIPTED: proposed for review.")
            .Role("medic", "SCRIPTED: environmental to this tree.")
            .Role("scribe", "SCRIPTED: recorded.")
            .Role("archivist", "SCRIPTED: recorded.");

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "coder", "verifier", "tester", "soldier",
            "builder", "medic", "scribe", "archivist", "fallback");

        var memory = new SqliteMemory(Path.Combine(_dir, $"record-{Guid.NewGuid():N}.db"));
        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);

        string? missionId = null;
        queen.RunMission("Add a short colony note to the documentation.",
            onMissionCreated: id => missionId = id);

        Assert.NotNull(missionId);
        return (memory, missionId!);
    }
}
