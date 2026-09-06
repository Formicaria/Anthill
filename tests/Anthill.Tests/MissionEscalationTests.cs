using Anthill.Core.Conversations;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE OPERATOR'S POLICY DOES NOT LAPSE WHEN THE WORK LEAVES THE CONVERSATION. v0.3.8.128.
///
/// `ConversationScopeTests` proves the gate stops a tool INSIDE an ambient scope. This file is
/// about the other half of the sentence `.102` wrote down and no release acted on: "a mission does
/// not run inside the ambient `ConversationScope`, so this branch is unreachable from one."
///
/// Read plainly, that says the colony's single tool chokepoint had an escalation gate that was
/// silent for every dispatch a mission ever made. An operator could set `Ask` on a conversation,
/// watch it escalate into a mission, and the mission would then apply patches, write files and run
/// shell commands with the gate they had just configured never once consulted.
///
/// Two things kept it from looking like a hole. `.102`–`.110` closed it BY HAND for the two execute
/// tools and the API's action path, so the loudest actions were covered and the chokepoint's
/// silence read as nothing being wrong — three call sites doing one job, which is defect class 5
/// waiting to happen. And `.105`'s question-filing hangs off the refusal branch, so a gate that
/// never refused also never ASKED: the operator saw no pending approval, which is indistinguishable
/// from a mission that had nothing to approve.
///
/// The tests are split deliberately. The first group is what now stops; the second is what
/// deliberately does NOT, because the safety of this change is entirely in how narrow it is — a
/// mission with no conversation has no operator policy to apply, and manufacturing `Ask` for it
/// would refuse every patch the coding lane has ever written.
/// </summary>
public class MissionEscalationTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;
    private readonly ToolRegistry _registry;

    public MissionEscalationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-mesc-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
        _registry = new ToolRegistry(_memory);
        _registry.Register(new SpyTool("apply_patch"));   // side-effecting
        _registry.Register(new SpyTool("system_info"));   // read-only
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Records whether it actually ran — the only way to prove a refusal had zero side effects.</summary>
    private sealed class SpyTool : ITool
    {
        public SpyTool(string name) => Name = name;
        public string Name { get; }
        public string Description => "spy";
        public int Calls { get; private set; }
        public ToolResult Run(IReadOnlyDictionary<string, object?> args)
        {
            Calls++;
            return new ToolResult(Name, true, "ran");
        }
    }

    private SpyTool Spy(string name) => (SpyTool)_registry.Tools.Single(t => t.Name == name);

    /// <summary>A saved mission, optionally owned by a conversation running under <paramref name="policy"/>.</summary>
    private Mission MissionUnder(EscalationPolicy? policy, bool attributed = true)
    {
        var mission = new Mission { Goal = "Apply the reviewed patch.", Status = MissionStatus.Running };
        _memory.SaveMission(mission);

        if (policy is not null)
            _memory.SaveConversation(new Conversation
            {
                Id = "c-" + Guid.NewGuid().ToString("N")[..8],
                Role = "queen",
                Policy = policy.Value,
                // An unattributed standing permission is one nobody can be shown to have given, and
                // `EffectivePolicy` folds it back to Ask. Exercised below.
                PolicySetBy = policy == EscalationPolicy.Ask || !attributed ? null : "zwright",
                PolicySetAt = policy == EscalationPolicy.Ask || !attributed ? null : DateTime.UtcNow,
                MissionIds = [mission.Id],
            });

        return mission;
    }

    // ---- what now stops ---------------------------------------------------------------------

    /// <summary>
    /// THE DEFECT, AS THE PROPERTY THAT WOULD HAVE CAUGHT IT.
    ///
    /// Ask, no answer, a mission belonging to that conversation: the patch is refused and the tool
    /// NEVER RUNS. A gate that lets the side effect happen and reports a refusal afterwards is not
    /// a gate — which is why the spy's call count is asserted rather than only the result.
    /// </summary>
    [Fact]
    public void AMissionOfAnAskingConversation_IsGated_EvenOutsideTheScope()
    {
        Assert.Null(ConversationScope.Current);   // exactly the condition that used to mean "ungoverned"
        var mission = MissionUnder(EscalationPolicy.Ask);

        var result = _registry.RunTool("apply_patch", missionId: mission.Id, antName: "queen");

        Assert.False(result.Success);
        Assert.Contains("escalation_refused", result.Error);
        Assert.Equal(FailureClass.AuthorizationFailure, result.Failure);
        Assert.Equal(0, Spy("apply_patch").Calls);
    }

    /// <summary>
    /// AND THE REFUSAL LEAVES THE OPERATOR SOMETHING TO ANSWER.
    ///
    /// This is the consequence that made the gap hard to see from the outside: `.105` files the
    /// question from the refusal branch, so a gate that never refused never asked, and an operator
    /// looking at their approvals saw a mission with nothing pending — the same picture a mission
    /// with nothing to approve produces. A refusal with no question is a mission stopped for a
    /// reason nobody can act on.
    /// </summary>
    [Fact]
    public void TheRefusal_FilesTheQuestion_RatherThanJustFailing()
    {
        var mission = MissionUnder(EscalationPolicy.Ask);
        _registry.RunTool("apply_patch", missionId: mission.Id, antName: "queen");

        var pending = _memory.ApprovalsForMission(mission.Id)
            .Where(a => a.ActionType == ApprovalActionType.ToolUse).ToList();

        Assert.Single(pending);
        Assert.Equal(ApprovalStatus.Pending, pending[0].Status);
        Assert.Equal($"{mission.Id}:apply_patch", pending[0].TargetId);
    }

    /// <summary>
    /// AND ANSWERING IT ACTUALLY UNBLOCKS THE STEP.
    ///
    /// The `.110` property, now reachable from the chokepoint rather than only from the two execute
    /// tools that read the ledger by hand. Without this the approval would be real, recorded,
    /// visible in the UI and inert — which is exactly the state `.105` shipped and `.110` fixed one
    /// tool at a time.
    /// </summary>
    [Fact]
    public void OnceTheOperatorApproves_TheSameCallProceeds()
    {
        var mission = MissionUnder(EscalationPolicy.Ask);
        Assert.False(_registry.RunTool("apply_patch", missionId: mission.Id, antName: "queen").Success);

        var request = _memory.ApprovalsForMission(mission.Id)
            .First(a => a.ActionType == ApprovalActionType.ToolUse);
        _memory.UpdateApprovalStatus(request.Id, ApprovalStatus.Approved, "yes");

        var second = _registry.RunTool("apply_patch", missionId: mission.Id, antName: "queen");

        Assert.True(second.Success, "an approved decision did not reach the dispatch path.");
        Assert.Equal(1, Spy("apply_patch").Calls);
    }

    /// <summary>
    /// A REJECTION IS AN ANSWER, and it does not re-open the question.
    ///
    /// The distinction `EscalationDecision.AwaitingDecision` exists for: both outcomes are
    /// `Allowed: false`, but only an ABSENCE is a question. A rejection that re-filed would ask the
    /// operator to say no once per attempt.
    /// </summary>
    [Fact]
    public void ARejection_StandsAndIsNotReAsked()
    {
        var mission = MissionUnder(EscalationPolicy.Ask);
        _registry.RunTool("apply_patch", missionId: mission.Id, antName: "queen");

        var request = _memory.ApprovalsForMission(mission.Id)
            .First(a => a.ActionType == ApprovalActionType.ToolUse);
        _memory.UpdateApprovalStatus(request.Id, ApprovalStatus.Rejected, "no");

        var second = _registry.RunTool("apply_patch", missionId: mission.Id, antName: "queen");

        Assert.False(second.Success);
        Assert.Equal(0, Spy("apply_patch").Calls);
        Assert.Single(_memory.ApprovalsForMission(mission.Id),
            a => a.ActionType == ApprovalActionType.ToolUse);
    }

    /// <summary>
    /// AN UNATTRIBUTED STANDING PERMISSION FAILS CLOSED, on the mission path too.
    ///
    /// `Conversation.EffectivePolicy` already folds an unauthored `AutoApprove` back to `Ask` — a
    /// permission nobody can be shown to have given is not one. This asserts the mission lane reads
    /// the EFFECTIVE policy rather than the stored one, because reading the stored field is the
    /// obvious way to write this and would grant exactly the permission that check exists to deny.
    /// </summary>
    [Fact]
    public void AnUnauthoredStandingPermission_DoesNotGrantAnything()
    {
        var mission = MissionUnder(EscalationPolicy.AutoApprove, attributed: false);

        var result = _registry.RunTool("apply_patch", missionId: mission.Id, antName: "queen");

        Assert.False(result.Success);
        Assert.Equal(0, Spy("apply_patch").Calls);
    }

    // ---- what deliberately does not stop ------------------------------------------------------

    /// <summary>
    /// A MISSION WITH NO CONVERSATION IS UNCHANGED, and this test is the safety of the whole change.
    ///
    /// Autonomous runs, the scheduler and the CLI have no conversation and therefore no operator
    /// policy. They are not ungoverned — `ToolAuthorization` and the mission's authority ceiling
    /// both ran before this gate — they simply have nothing here to consult. Inventing `Ask` for
    /// them would refuse every patch the coding lane has ever written, on the grounds that a
    /// conversation nobody started did not answer a question nobody asked.
    /// </summary>
    [Fact]
    public void AMissionWithNoConversation_RunsExactlyAsItDid()
    {
        var mission = MissionUnder(policy: null);

        var result = _registry.RunTool("apply_patch", missionId: mission.Id, antName: "queen");

        Assert.True(result.Success,
            "the mission lane now gates work that has no operator policy behind it — this narrows "
          + "the colony to a standstill rather than narrowing what a conversation may do.");
        Assert.Equal(1, Spy("apply_patch").Calls);
    }

    /// <summary>
    /// A STANDING PERMISSION ALLOWS IT — WITH THE RECORD OF WHO GRANTED IT.
    ///
    /// `.46`'s rule: permission IS the record. An action taken under a standing decision carries
    /// the decision that permitted it, so "why was the colony allowed to do that" never answers
    /// "nobody knows". The refusal branch has logged its own event since `.105`; the allowed half
    /// had no equivalent because nothing on this path could produce one.
    /// </summary>
    [Fact]
    public void AStandingPermission_Allows_AndRecordsWhoseAuthorityItWas()
    {
        var mission = MissionUnder(EscalationPolicy.Bypass);

        var result = _registry.RunTool("apply_patch", missionId: mission.Id, antName: "queen");

        Assert.True(result.Success);
        Assert.Equal(1, Spy("apply_patch").Calls);

        var allowed = _memory.GetRecentEvents(50, "escalation_allowed", mission.Id);
        Assert.NotEmpty(allowed);
        Assert.DoesNotContain(_memory.ApprovalsForMission(mission.Id),
            a => a.ActionType == ApprovalActionType.ToolUse);
    }

    /// <summary>
    /// READS ARE NOT GATED, and the set of what is comes from `EscalationGate` rather than from a
    /// second opinion held here. A colony that asked the operator about every lookup would bury the
    /// decisions that matter under the ones that do not.
    /// </summary>
    [Fact]
    public void AReadOnlyTool_IsNeverEscalated_EvenUnderAsk()
    {
        var mission = MissionUnder(EscalationPolicy.Ask);

        var result = _registry.RunTool("system_info", missionId: mission.Id, antName: "queen");

        Assert.True(result.Success);
        Assert.Equal(1, Spy("system_info").Calls);
        Assert.DoesNotContain(_memory.ApprovalsForMission(mission.Id),
            a => a.ActionType == ApprovalActionType.ToolUse);
        Assert.False(EscalationGate.NeedsDecision("system_info"));
        Assert.True(EscalationGate.NeedsDecision("apply_patch"));
    }

    // ---- the gate is wired, and wired ONCE -----------------------------------------------------

    /// <summary>
    /// THE READ HAPPENS AT THE CHOKEPOINT, NOT PER TOOL. Defect class 2 and 5 together.
    ///
    /// `Governing` is only worth anything if the dispatch path calls it — a gate declared and
    /// reaching nobody is the exact shape `.102` left behind and this release is repaying. And it
    /// has to be called from ONE place: `.102` closed this hole for the execute tools by hand, and
    /// a fourth and fifth hand-rolled copy is how a colony ends up with gates that disagree about
    /// what the operator said.
    ///
    /// Source-scanned with comments stripped, because the property is about the code and this file
    /// is full of prose naming the same symbols.
    /// </summary>
    [Fact]
    public void TheMissionLaneGate_IsCalledFromTheDispatchChokepointAndOnlyThere()
    {
        var callers = Directory
            .EnumerateFiles(Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => (path, code: SourceText.CodeOnly(File.ReadAllText(path))))
            .Where(f => f.code.Contains("OperatorDecisions.Governing(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f.path))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal("Tools.cs", Assert.Single(callers));

        var dispatch = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Tools", "Tools.cs")));

        // Immediately after the ambient gate, and only when that gate had nothing to say — the
        // ambient answer must win, because an operator sitting in the conversation has answered
        // more recently than any record of it.
        var ambient = dispatch.IndexOf("ConversationScope.Evaluate(name)", StringComparison.Ordinal);
        var durable = dispatch.IndexOf("OperatorDecisions.Governing(", StringComparison.Ordinal);
        Assert.True(ambient > 0 && durable > ambient,
            "the durable read must come after the ambient one, or a live answer loses to a stored one.");
        Assert.Contains("escalation is null && missionId is not null", dispatch, StringComparison.Ordinal);

        // VACUITY FLOOR: the sweep actually read the tree.
        Assert.True(Directory
            .EnumerateFiles(Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Count() > 200, "the source sweep found almost nothing and would pass on an empty tree.");
    }
}
