using System;
using System.IO;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE KNOWLEDGE SCOPE IS IN FORCE BEFORE THE MISSION IS CLASSIFIED. v0.3.9.9.
///
/// THIS GUARD EXISTS BECAUSE v0.3.9.8 SHIPPED A FIX THAT WAS GREEN AND INERT.
///
/// `.9.8` taught intake that a question ABOUT the knowledge base is not a `simple_answer` — the one
/// class from which the knowledge step is excluded — by asking
/// `KnowledgeScopeContext.HasScope`. Its tests passed. It changed nothing in production, and the
/// operator's next Study pass planned `builder -> verifier` exactly as before.
///
/// `MissionContext.Create` classifies the mission. `Queen.RunMission` called it on one line and
/// entered the knowledge scope a hundred and thirty lines further down — late enough for every ant's
/// tool call, which is all `.136` needed when it put the entry there, and far too late for intake.
/// So `HasScope` was false at the only moment it was consulted.
///
/// AND THE TEST THAT SHOULD HAVE CAUGHT IT CAUSED IT. `SeededStudyMissionTests` entered the scope
/// itself before calling `MissionIntake.Resolve`, because that is the obvious way to test a function
/// that reads an ambient. It built a world production never creates. A unit test that establishes
/// the precondition it is verifying is not a weak test — it is a test of a different program.
///
/// SO THIS ONE ASSERTS THE ORDER, IN THE SOURCE, because ordering is invisible at both sites: the
/// line that enters the scope is correct, the line that classifies is correct, and only their
/// sequence is wrong. There is nothing to observe at runtime short of running a whole mission
/// against a live model, and a guard that needs a colony is a guard that gets skipped.
/// </summary>
public class QueenKnowledgeScopeOrderTests
{
    private static string Queen() => SourceText.CodeOnly(File.ReadAllText(
        Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));

    [Fact]
    public void TheScopeIsEntered_BeforeTheMissionContextIsCreated()
    {
        var queen = Queen();

        var enter = queen.IndexOf("KnowledgeScopeContext.Enter(knowledgeScope)", StringComparison.Ordinal);
        var create = queen.IndexOf("MissionContext.Create(mission, profile, missionStartedAt, contract)",
            StringComparison.Ordinal);

        Assert.True(enter > 0, "Queen no longer enters a knowledge scope at all.");
        Assert.True(create > 0, "Queen no longer creates the mission context where this guard expects it.");

        Assert.True(enter < create,
            "Queen enters the knowledge scope AFTER building the mission context. The context is "
          + "where the mission is classified, and intake asks `KnowledgeScopeContext.HasScope` to "
          + "decide whether a question about the knowledge base can be a `simple_answer`. With the "
          + "entry below it, that question is always answered 'no scope' — which is how v0.3.9.8 "
          + "shipped a correct fix that did nothing at all. The scope must be in force before "
          + "anything classifies.");
    }

    /// <summary>
    /// ONE ENTRY, so "before" means something. Two would let the second be the one that matters
    /// while this guard was satisfied by the first.
    /// </summary>
    [Fact]
    public void ThereIsExactlyOnePlaceThatEntersIt()
    {
        var queen = Queen();

        var count = 0;
        for (var i = queen.IndexOf("KnowledgeScopeContext.Enter", StringComparison.Ordinal);
             i >= 0;
             i = queen.IndexOf("KnowledgeScopeContext.Enter", i + 1, StringComparison.Ordinal))
            count++;

        Assert.True(count == 1,
            $"Queen enters a knowledge scope {count} times. Ordering guarantees stop meaning "
          + "anything once there is more than one entry, and two ambients for one mission is a "
          + "second answer to which knowledge it may read.");
    }
}
