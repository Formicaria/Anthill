using Anthill.SDK.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The operator talks to the COLONY. The colony dispatches a coding agent as a tool. v0.3.8.57.
///
/// THE SHAPE THAT WAS WRONG. `conversation` is a route key like `coder` or `planner`, and the router
/// treats every provider identically because from its side they are: ask, receive text. So pointing
/// `conversation` at an installed agent CLI made the chat box a DIRECT LINE to that agent. A message
/// went to Claude Code, which answered — and, being an agent, could also edit the working tree. No
/// task, no plan, no ui_map, no tester, no soldier, no verifier anywhere in the sequence. The colony
/// reduced to a text field in front of someone else's tool.
///
/// v0.3.8.53 saw the consequence and contained it: changes from that lane became one canonical
/// `direct_change` artifact, explicitly unverified, never feeding positive memory. That was the right
/// response to the symptom and left the shape intact — the lane still existed, it was just labelled.
///
/// This closes the shape. An agent CLI is the most capable thing the colony can dispatch and the
/// coder role still routes to one; everything downstream of the coder — patch set, soldier, tester,
/// verifier, revision-bound evidence — still applies to its work. What cannot happen is the operator
/// addressing it directly, because that path has none of those.
/// </summary>
public class ChatSpeaksToTheColonyTests
{
    // -------------------------------------------------------------------------------------------
    // The marker
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The provider DECLARES what it is. The alternative was a list of agent ids in the core, which
    /// the layering forbids (the core may not name a provider implementation) and which would rot:
    /// a new agent CLI would be uncontained until somebody remembered to add it to the list.
    /// </summary>
    [Fact]
    public void TheAgentCliProvider_DeclaresItselfAnAutonomousCodingAgent()
    {
        var provider = typeof(Anthill.Modules.Reasoning.AgentCliProvider);

        Assert.True(typeof(IAutonomousCodingAgent).IsAssignableFrom(provider),
            "AgentCliProvider no longer declares IAutonomousCodingAgent, so the runtime can no longer "
          + "tell it apart from a chat model and the conversation route would silently accept it.");
    }

    /// <summary>
    /// And an ordinary reasoning provider does NOT. Without this the marker could be satisfied by
    /// everything, and the refusal below would block all chat rather than one lane.
    /// </summary>
    [Theory]
    [InlineData(typeof(Anthill.Modules.Reasoning.OllamaClient))]
    public void OrdinaryProviders_AreNotMarkedAsCodingAgents(Type provider) =>
        Assert.False(typeof(IAutonomousCodingAgent).IsAssignableFrom(provider));

    /// <summary>
    /// The marker lives in the SDK, which is what lets the core test for it without naming a module.
    /// If it drifted into `Anthill.Modules.Reasoning`, the core could not reference it and the check
    /// would have to be reimplemented as the id list this design exists to avoid.
    /// </summary>
    [Fact]
    public void TheMarker_LivesInTheSdkSoTheCoreCanSeeIt() =>
        Assert.Equal("Anthill.SDK.Reasoning", typeof(IAutonomousCodingAgent).Namespace);

    // -------------------------------------------------------------------------------------------
    // The refusal
    // -------------------------------------------------------------------------------------------

    private static string QueenSource() => SourceText.CodeOnly(File.ReadAllText(
        Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));

    /// <summary>
    /// The conversation `ask` refuses a coding agent. This is the whole change; everything else is
    /// scaffolding around it.
    /// </summary>
    [Fact]
    public void TheConversationRoute_RefusesAnAutonomousCodingAgent()
    {
        var queen = QueenSource();

        Assert.Contains("IAutonomousCodingAgent agent", queen);
        Assert.Contains("Chat is not wired to", queen);
    }

    /// <summary>
    /// REFUSED, not silently rerouted.
    ///
    /// Falling back to another provider would leave an operator believing they were talking to the
    /// agent they configured, getting worse answers than they expected, with nothing anywhere
    /// explaining why. The refusal is the honest option — and it is only tolerable because the
    /// message says what to change and what to do instead, which is asserted here rather than
    /// assumed.
    /// </summary>
    [Fact]
    public void TheRefusal_SaysWhatToChangeAndWhatToDoInstead()
    {
        var queen = QueenSource();

        // Fragments that survive line-wrapping. The first draft asserted "the colony will start a
        // mission for it", which is one sentence in the REPLY and two string literals in the SOURCE
        // — so the test failed on how the message happens to be wrapped rather than on what it says.
        // A guard that breaks when a line is re-flowed is a formatting check wearing a content
        // check's name, and it gets weakened rather than read the next time it fires.
        //
        // Where the operator fixes it.
        Assert.Contains("Providers & Model Routing", queen);
        // That the capability is not being removed — it moves to where the review lives.
        Assert.Contains("leave `coder` pointed at", queen);
        // And that asking here still gets the work done, by the colony.
        Assert.Contains("start a mission for it", queen);

        // The refusal must NOT reroute: no second GetClientForProvider call rescuing the turn.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(queen, @"GetClientForProvider\(").Count);
    }

    /// <summary>
    /// The colony keeps the agent. This is the half that makes the rule a boundary rather than a ban
    /// — `coder` is still routable to an agent CLI, and its output still goes through the patch set,
    /// the soldier, the tester and the verifier.
    /// </summary>
    [Fact]
    public void TheColonyMayStillDispatchAnAgent()
    {
        Assert.Contains("coder", Anthill.Core.Configuration.AnthillRuntime.RoutableRoles);
        Assert.Contains("conversation", Anthill.Core.Configuration.AnthillRuntime.RoutableRoles);

        // The refusal is scoped to the conversation route only — nothing about it touches the roles.
        var queen = QueenSource();
        var refusal = queen.IndexOf("IAutonomousCodingAgent agent", StringComparison.Ordinal);
        var route = queen.IndexOf("GetRoute(\"conversation\")", StringComparison.Ordinal);

        Assert.True(route >= 0 && refusal > route,
            "the coding-agent refusal is no longer inside the conversation route's resolution, so it "
          + "is either unreachable or has widened to a path it was never reasoning about.");
    }

    /// <summary>
    /// The route list's own comment says the rule. Config and runtime disagreeing about whether an
    /// agent may serve chat is precisely the "declared one thing, does another" defect this release
    /// spent its length removing — and a comment is what an operator reads before the code.
    /// </summary>
    [Fact]
    public void TheRoutableRoleList_DocumentsTheRule()
    {
        var runtime = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs"));

        Assert.Contains("is not a valid answer to \"who speaks for the colony", runtime);
    }
}
