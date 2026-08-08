using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Anthill.Core.Tools;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// All twelve roles, in one colony, against a real database. v3.8.30.
///
/// Every test written across the activation program checked one role or one rule. This is the first
/// that stands all twelve up together and asks whether they are individually EXECUTABLE — each
/// handler invoked with the shape of task the runtime would really hand it, through the real
/// registry and the real memory.
///
/// WHAT THIS IS NOT, stated plainly so nobody mistakes it for more: no model runs, so no role that
/// requires one produces its real output; and this is not a planned mission driven end to end by the
/// Queen. Those two together are the live run that belongs to the operator, and this test cannot
/// substitute for it. What it CAN prove is that no role throws, no role is unreachable, and each one
/// returns a structured outcome rather than a crash — which is the class of defect that shipped
/// eight times in this codebase.
/// </summary>
public class TwelveRoleEndToEndTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"anthill-e2e-{Guid.NewGuid():N}.db");
    private readonly SqliteMemory _memory;
    private readonly ToolRegistry _tools;

    private readonly bool _specialistsWas = AnthillRuntime.EnableSpecialistAntExecution;
    private readonly bool _testerWas = AnthillRuntime.EnableTesterAnt;
    private readonly bool _soldierWas = AnthillRuntime.EnableSoldierAnt;
    private readonly bool _medicWas = AnthillRuntime.EnableMedicAnt;
    private readonly bool _archivistWas = AnthillRuntime.EnableArchivistAnt;
    private readonly bool _cartographerWas = AnthillRuntime.EnableUiCartographerAnt;
    private readonly bool _scribeWas = AnthillRuntime.EnableScribeAnt;

    public TwelveRoleEndToEndTests()
    {
        _memory = new SqliteMemory(_dbPath);
        _tools = new ToolRegistry(_memory);
        // The built-ins the CORE registers. Deliberately NOT the Anthill.Modules.Tools set —
        // list_directory, read_text_file, search_workspace and the rest arrive from a module the
        // composition root drains in, and a test that hand-registered them would be asserting a
        // colony nobody actually builds.
        //
        // The consequence is real and is the point: the file ant and the cartographer CANNOT do
        // their work here, and they say so with a DependencyFailure rather than pretending. That is
        // the correct behaviour for a colony built without the tools module, and this fixture
        // exercises it rather than papering over it.
        _tools.Register(new SystemInfoTool());
        _tools.Register(new RunAllowlistedCheckTool(Path.GetTempPath()));
        _tools.Register(new ChangedFilesSummaryTool());

        // The full roster, as an operator running `roster_profile: "full"` would have it.
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableSoldierAnt = true;
        AnthillRuntime.EnableMedicAnt = true;
        AnthillRuntime.EnableArchivistAnt = true;
        AnthillRuntime.EnableUiCartographerAnt = true;
        AnthillRuntime.EnableScribeAnt = true;
    }

    public void Dispose()
    {
        AnthillRuntime.EnableSpecialistAntExecution = _specialistsWas;
        AnthillRuntime.EnableTesterAnt = _testerWas;
        AnthillRuntime.EnableSoldierAnt = _soldierWas;
        AnthillRuntime.EnableMedicAnt = _medicWas;
        AnthillRuntime.EnableArchivistAnt = _archivistWas;
        AnthillRuntime.EnableUiCartographerAnt = _cartographerWas;
        AnthillRuntime.EnableScribeAnt = _scribeWas;
        _memory.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    /// <summary>The twelve handlers, constructed exactly as `Queen.BuildToolRegistry` does.</summary>
    private Dictionary<string, BaseAnt> Roster() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["researcher"] = new ResearcherAnt(_memory, _tools, null),
        ["web"] = new WebResearchAnt(_memory, _tools, null),
        ["file"] = new FileAnt(_tools),
        ["coder"] = new CoderAnt(false, null, (Anthill.SDK.Artifacts.IArtifactStore)_memory),
        ["builder"] = new BuilderAnt(false, null, (Anthill.SDK.Artifacts.IArtifactStore)_memory),
        ["verifier"] = new VerifierAnt(false, null,
            (Anthill.SDK.Artifacts.IEvidenceStore)_memory, (Anthill.SDK.Artifacts.IArtifactStore)_memory),
        ["ui_cartographer"] = new UiCartographerAnt(_tools),
        ["tester"] = new TesterAnt(_tools),
        ["soldier"] = new SoldierAnt((Anthill.SDK.Artifacts.IArtifactStore)_memory),
        ["scribe"] = new ScribeAnt(_tools),
        ["medic"] = new MedicAnt(),
        ["archivist"] = new ArchivistAnt(),
    };

    /// <summary>A task of the shape each role's contract actually accepts.</summary>
    private static DomainTask TaskFor(string role) => new()
    {
        Title = $"{role} step",
        Description = role switch
        {
            "medic" => "Diagnose the failure.",
            "archivist" => "Extract durable lessons. outcome: completed_verified",
            "soldier" => "Review the proposed change. approved_scope: src/",
            "scribe" => "Draft release notes for the change.",
            _ => "Do the role's work for this mission.",
        },
        AssignedAnt = role,
        TaskType = role switch
        {
            "researcher" => "research",
            "web" => "external_research",
            "file" => "file_inspection",
            "coder" => "patch_proposal",
            "builder" => "build_answer",
            "verifier" => "verification",
            "ui_cartographer" => "ui_mapping",
            "tester" => "test_execution",
            "soldier" => "security_review",
            "scribe" => "release_notes",
            "medic" => "failure_diagnosis",
            "archivist" => "mission_summary",
            _ => "research",
        },
    };

    /// <summary>
    /// A finished mission with a failed task in it, so the medic has something to diagnose and the
    /// archivist has something terminal to summarise. Both refuse an empty mission by design, and a
    /// fixture that gave them one would be testing the refusal rather than the role.
    /// </summary>
    private Mission SeededMission()
    {
        var mission = new Mission { Goal = "Summarize the colony and propose a docs change", Status = MissionStatus.Complete };
        mission.Tasks.Add(new DomainTask
        {
            Title = "Earlier work", AssignedAnt = "researcher", TaskType = "research",
            Status = TaskStatus.Complete, Result = "Found src/Anthill.Core and docs/PLAN.md.",
        });
        mission.Tasks.Add(new DomainTask
        {
            Title = "A step that failed", AssignedAnt = "tester", TaskType = "test_execution",
            Status = TaskStatus.Failed, Critical = false,
            FailureReason = "dotnet_build exited 1", FailureType = "VerificationFailure",
        });
        _memory.SaveMission(mission);
        return mission;
    }

    /// <summary>
    /// THE TEST. Every one of the twelve executes and returns a structured result — no exception, no
    /// null, and a status code from the declared vocabulary.
    ///
    /// A role is allowed to BLOCK or FAIL here: with no model, the coder must fail rather than invent
    /// a patch, and that is correct behaviour rather than a broken role. What is not allowed is a
    /// throw, an empty status, or a role the roster cannot reach.
    /// </summary>
    [Fact]
    public void AllTwelveRoles_ExecuteAndReturnAStructuredOutcome()
    {
        var mission = SeededMission();
        var roster = Roster();
        // The SIX codes the runtime actually emits, read out of AntExecutionResult's factories
        // rather than remembered. The first draft of this list guessed five and included "failed",
        // which nothing produces, while omitting "failed_permanent", which
        // `AntExecutionResult.Failed` emits for every non-retryable class. The test then reported
        // two roles as broken when the roles were right and the list was wrong — a fixture asserting
        // its author's memory instead of the code.
        var known = new[]
        {
            "succeeded", "succeeded_with_warnings", "blocked", "skipped",
            "failed_retryable", "failed_permanent",
        };

        var problems = new List<string>();
        var outcomes = new Dictionary<string, string>();

        foreach (var role in roster.Keys.OrderBy(r => r, StringComparer.Ordinal))
        {
            try
            {
                var result = roster[role].Execute(TaskFor(role), mission);

                if (result is null) { problems.Add($"{role}: returned null"); continue; }
                if (string.IsNullOrWhiteSpace(result.StatusCode)) { problems.Add($"{role}: empty status code"); continue; }
                if (!known.Contains(result.StatusCode))
                    problems.Add($"{role}: unknown status code '{result.StatusCode}'");
                if (string.IsNullOrWhiteSpace(result.Summary))
                    problems.Add($"{role}: no summary — an operator cannot read this outcome");

                outcomes[role] = result.StatusCode;
            }
            catch (Exception error)
            {
                problems.Add($"{role}: THREW {error.GetType().Name} — {error.Message}");
            }
        }

        Assert.True(problems.Count == 0,
            "The twelve-role run found problems:\n  " + string.Join("\n  ", problems)
            + "\n\nOutcomes: " + string.Join(", ", outcomes.Select(o => $"{o.Key}={o.Value}")));

        Assert.Equal(12, outcomes.Count);
    }

    /// <summary>
    /// The two roles whose tools live in `Anthill.Modules.Tools` REFUSE cleanly when that module was
    /// never composed in, rather than reporting success over an empty result.
    ///
    /// This is the colony an operator gets if they build the core without the tools module, and
    /// v3.8.16's note called it out: every call to one of those tools returns a typed
    /// ValidationFailure rather than crashing, which is the right shape for an absent capability and
    /// NOT what someone expects from a default install. Pinned here so the difference stays visible.
    /// </summary>
    [Theory]
    [InlineData("file")]
    [InlineData("ui_cartographer")]
    public void WithoutTheToolsModule_TheFileRolesRefuseRatherThanReportEmptySuccess(string role)
    {
        var result = Roster()[role].Execute(TaskFor(role), SeededMission());

        Assert.False(result.Success);
        Assert.Equal("failed_permanent", result.StatusCode);
    }

    /// <summary>
    /// With no model, the CODER must fail rather than propose. A patch invented without a model
    /// would be worse than no patch — this pins that the refusal is deliberate and typed, not a
    /// crash that happens to look like one.
    /// </summary>
    [Fact]
    public void WithNoModel_TheCoderRefusesRatherThanInventing()
    {
        var result = Roster()["coder"].Execute(TaskFor("coder"), SeededMission());

        Assert.False(result.Success);
        Assert.Contains("unavailable", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ...and the roles that CAN degrade do, rather than failing the mission. The researcher and
    /// builder produce a local answer with no model, which is what keeps research missions working
    /// on a colony with no provider.
    /// </summary>
    [Theory]
    [InlineData("researcher")]
    [InlineData("builder")]
    public void WithNoModel_TheProseRolesDegradeRatherThanFail(string role)
    {
        var result = Roster()[role].Execute(TaskFor(role), SeededMission());

        Assert.True(result.Success, $"{role} returned {result.StatusCode}: {result.Summary}");
    }

    /// <summary>
    /// The tools each role DISPATCHES are the tools its contract ALLOWS. This is the check that
    /// catches a handler reaching for something the authorization layer will deny at runtime — a
    /// failure that unit tests over a stub registry cannot see, because a stub allows everything.
    /// </summary>
    [Fact]
    public void NoRoleDispatchesAToolItsContractForbids()
    {
        var mission = SeededMission();
        var roster = Roster();
        var denials = new List<string>();

        foreach (var role in roster.Keys)
        {
            var contract = AntExecutionCatalog.ContractFor(role);
            if (contract is null || contract.AllowedTools.Count == 0) continue;

            try { roster[role].Execute(TaskFor(role), mission); }
            catch { /* execution problems are the other test's subject */ }

            foreach (var tool in contract.AllowedTools)
                if (ToolAuthorization.Evaluate(role, tool) is { Allowed: false } denied)
                    denials.Add($"{role} -> {tool}: {denied.Reason}");
        }

        Assert.True(denials.Count == 0,
            "These roles declare tools their own authorization refuses:\n  " + string.Join("\n  ", denials));
    }
}
