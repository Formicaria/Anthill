using Xunit;

namespace Anthill.Tests;

/// <summary>
/// EVERY OPERATOR MESSAGE IS A MISSION. v0.3.8.58.
///
/// THE SHAPE THAT WAS WRONG. `conversation` was a route key like `coder` or `planner`, and the
/// router treats every provider identically because from its side they are identical: ask, receive
/// text. So pointing `conversation` at an installed agent CLI made the chat box a direct line to
/// that agent. A message went to Claude Code, which answered — and, being an agent standing in a
/// real directory with the operator's approval policy, could also edit the working tree. No task,
/// no plan, no ui_map, no tester, no soldier, no verifier anywhere in the sequence. The colony
/// reduced to a text field in front of someone else's tool.
///
/// TWO RELEASES TRIED TO CLOSE IT AND CLOSED SOMETHING ELSE.
///
/// v0.3.8.53 saw the consequence and contained it: changes from that lane became one canonical
/// `direct_change` artifact, explicitly unverified, never feeding positive memory. Correct about
/// the symptom, and it left the shape intact — the lane still existed, it was just labelled.
///
/// v0.3.8.57 blocked an autonomous coding agent from serving the route, and rewrote the chat prompt
/// to say the lane had no tools and changed nothing. The first narrowed WHO could bypass the
/// colony. The second changed what a model was TOLD. Neither was the authorisation, which lived in
/// `AgentAccessScope.Enter(..., confinedWorkspace: false)` and in a hundred lines of direct-edit
/// sweep whose entire job was to notice which files a chat turn had written and commit them. The
/// tests in that release asserted on the prompt's wording, so they passed. A guard that reads prose
/// to decide whether prose is load-bearing is the defect it is looking for.
///
/// WHAT REPLACES ALL OF IT. There is no second lane to secure. `Run` sends every message down the
/// mission path; the planner decides the shape of the work; the answer the operator reads is the
/// scribe's, recorded back into the conversation after the mission settles. The colony deciding a
/// message is small is a different thing from a lane in front of the colony deciding it never had
/// to ask, and only the first keeps twelve roles load-bearing.
///
/// The approval policy still governs MISSIONS. Skip-all means a verified patch applies without a
/// card; it never meant the chat box may edit files. Those two were one sentence, which is how an
/// operator granted the second while meaning the first.
/// </summary>
public class ChatSpeaksToTheColonyTests
{
    private static string RunnerSource() => SourceText.CodeOnly(File.ReadAllText(
        Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Conversations", "ConversationRunner.cs")));

    private static string QueenSource() => SourceText.CodeOnly(File.ReadAllText(
        Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));

    // -------------------------------------------------------------------------------------------
    // There is no route to point at an agent
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE CONFIGURATION SURFACE IS GONE, which is the strongest available form of this fix.
    ///
    /// v0.3.8.57 kept the `conversation` route and refused an agent for it at dispatch. That shape
    /// still offers the choice in the console, still lets the operator pick it, and explains
    /// afterwards why the thing they were allowed to configure does not work. An option that cannot
    /// be selected cannot be selected wrongly.
    /// </summary>
    [Fact]
    public void ThereIsNoConversationRoute_ToPointAtAnything() =>
        Assert.DoesNotContain("conversation", Anthill.Core.Configuration.AnthillRuntime.RoutableRoles);

    /// <summary>
    /// The colony KEEPS the agent. This is the half that makes the change a boundary rather than a
    /// ban: `coder` still routes to an agent CLI deliberately, and everything downstream of the
    /// coder — patch set, soldier, tester, verifier, revision-bound evidence — still applies to
    /// what it produces. What is removed is the operator addressing it directly.
    /// </summary>
    [Fact]
    public void TheColonyMayStillDispatchAnAgentAsATool() =>
        Assert.Contains("coder", Anthill.Core.Configuration.AnthillRuntime.RoutableRoles);

    /// <summary>
    /// And the Queen composes the runner with NO chat model. The `ask` delegate resolved the
    /// conversation route and handed a prompt to whatever served it; it is what made the chat box a
    /// place where work could happen.
    /// </summary>
    [Fact]
    public void TheQueen_ComposesTheRunnerWithNoChatModel()
    {
        var queen = QueenSource();

        Assert.DoesNotContain("GetRoute(\"conversation\")", queen);
        Assert.DoesNotContain("ask:", queen);
    }

    // -------------------------------------------------------------------------------------------
    // Every message is a mission
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The runner holds no reasoning delegate at all. A field is a stronger subject than a sentence:
    /// as long as one existed, the question was only ever "under what conditions is it called".
    /// </summary>
    [Fact]
    public void TheRunner_HoldsNoReasoningDelegate()
    {
        var runner = RunnerSource();

        Assert.DoesNotContain("ConversationReply", runner);
        Assert.DoesNotContain("_ask", runner);
    }

    /// <summary>
    /// No prompt is built here, and no escalation marker is parsed.
    ///
    /// The marker deserves its own line because it was the previous design's best idea: the model
    /// ended a reply with `[[START_MISSION]]` to propose real work, which fixed the genuinely worse
    /// thing before it (the colony telling its operator to "ask for it as a mission explicitly", a
    /// magic word). But it left the model holding the decision about whether the colony's own
    /// pipeline was necessary — the one decision a model must never make about itself.
    /// </summary>
    [Fact]
    public void NoChatPromptIsBuilt_AndNoEscalationMarkerIsParsed()
    {
        var runner = RunnerSource();

        Assert.DoesNotContain("ChatPrompt", runner);
        Assert.DoesNotContain("START_MISSION", runner);
    }

    /// <summary>
    /// The runtime behaviour, not the source: a bare message with no mode requested reaches the
    /// mission pipeline. Every assertion above is a source-level detector, and source detectors
    /// prove a thing is absent rather than that the remaining path works.
    /// </summary>
    [Fact]
    public void ABareMessage_ReachesTheMissionPipeline()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-chatmission-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            using var memory = new Anthill.Core.Memory.SqliteMemory(Path.Combine(dir, "memory.db"));
            var conversation = new Anthill.Core.Conversations.Conversation
            {
                Id = "c1", Role = "queen",
                Policy = Anthill.Core.Conversations.EscalationPolicy.Bypass,
                PolicySetBy = "zwright", PolicySetAt = DateTime.UtcNow,
            };
            memory.SaveConversation(conversation);

            var goals = new List<string>();
            var runner = new Anthill.Core.Conversations.ConversationRunner(memory,
                (goal, onCreated, _) => { goals.Add(goal); onCreated("m1"); return "m1"; });

            // No mode argument at all — the default is what an un-updated caller sends.
            var outcome = runner.Run(conversation, "what does this repository do?");

            Assert.Equal(Anthill.Core.Conversations.ConversationMode.Mission, outcome.Mode);
            Assert.Equal("m1", outcome.MissionId);
            Assert.Contains("what does this repository do?", Assert.Single(goals));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
