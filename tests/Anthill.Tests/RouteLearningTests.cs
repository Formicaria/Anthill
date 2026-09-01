using Anthill.Core.Memory;
using Anthill.Core.Outcomes;
using Anthill.Core.Pheromones;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A LEARNED ROUTE IMPROVES A LATER MISSION AND OVERRIDES NOTHING. v0.3.8.107, PLAN.md §2b `.107`.
///
/// THE EXIT GATE: "a learned route improves a compatible later mission without overriding
/// compatibility, authority or evidence."
///
/// THE FINDING THAT SHAPED THIS RELEASE. Route trails have been written on every model call since
/// the router existed, and the obvious implementation of this gate was to read them. They cannot
/// carry the claim. `ModelRouter` pays a `model_route` trail's positive delta on `result.Ok` — the
/// provider answered without erroring — while a WORKER trail is paid only for `completed_verified`,
/// which is the entire reason `.93`'s selection rule is sound. A model that answers promptly,
/// fluently and wrongly carries a strong `model_route` trail forever.
///
/// So `.107` writes a second signal rather than reinterpreting the first: `verified_route`, credited
/// at the one site that pays only for a verified mission, to the routes that mission actually used.
/// The two kinds mean different things and are kept apart on purpose — collapsing them would be an
/// overclaim with six releases of history behind it.
/// </summary>
public class RouteLearningTests : IDisposable
{
    private readonly string _dir;

    public RouteLearningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-route-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private SqliteMemory Memory() => new(Path.Combine(_dir, $"r-{Guid.NewGuid():N}.db"));

    private static RouteGuidedSelection.Candidate Fit(string provider, string model) =>
        new(provider, model, Compatible: true);

    private static RouteGuidedSelection.Candidate Unfit(string provider, string model) =>
        new(provider, model, Compatible: false);

    /// <summary>A trail lookup built from a literal map — the purity `.93` was designed for.</summary>
    private static Func<string, TrailView?> Trails(params (string Key, TrailView View)[] entries)
    {
        var map = entries.ToDictionary(e => e.Key, e => e.View, StringComparer.Ordinal);
        return key => map.GetValueOrDefault(key);
    }

    private static string Key(string role, string provider, string model) =>
        RouteGuidedSelection.TrailKeyFor(role, provider, model);

    // -------------------------------------------------------------------------------------------
    // The improvement
    // -------------------------------------------------------------------------------------------

    /// <summary>THE POSITIVE. A verified trail above baseline, with net successes, wins.</summary>
    [Fact]
    public void AVerifiedRoute_IsPreferredOverAnUnprovenOne()
    {
        var preferred = RouteGuidedSelection.Prefer(
            new[] { Fit("ollama", "small"), Fit("openai", "good") },
            Trails((Key("coder", "openai", "good"), new TrailView(0.72, 9, 1))),
            "coder");

        Assert.NotNull(preferred);
        Assert.Equal("openai", preferred!.Provider);
        Assert.Equal("good", preferred.Model);
    }

    /// <summary>And the STRONGEST verified route wins among several.</summary>
    [Fact]
    public void TheStrongestVerifiedRoute_Wins()
    {
        var preferred = RouteGuidedSelection.Prefer(
            new[] { Fit("a", "m1"), Fit("b", "m2"), Fit("c", "m3") },
            Trails(
                (Key("coder", "a", "m1"), new TrailView(0.61, 3, 1)),
                (Key("coder", "b", "m2"), new TrailView(0.88, 12, 2)),
                (Key("coder", "c", "m3"), new TrailView(0.55, 2, 1))),
            "coder");

        Assert.Equal("b", preferred!.Provider);
    }

    // -------------------------------------------------------------------------------------------
    // Evidence: what does NOT qualify
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// AN UNWRITTEN TRAIL NEVER WINS, and neither does one sitting exactly at the baseline. A new
    /// trail starts at 0.5 and only verified missions push it above; qualification is
    /// strictly-greater so "never used" can never outrank "configured".
    /// </summary>
    [Fact]
    public void AnUnprovenRoute_NeverWins()
    {
        Assert.Null(RouteGuidedSelection.Prefer(
            new[] { Fit("a", "m1"), Fit("b", "m2") }, Trails(), "coder"));

        Assert.Null(RouteGuidedSelection.Prefer(
            new[] { Fit("a", "m1") },
            Trails((Key("coder", "a", "m1"), new TrailView(RouteGuidedSelection.BaselineStrength, 5, 0))),
            "coder"));
    }

    /// <summary>
    /// NET EVIDENCE MUST BE POSITIVE. A route with a high strength and more failures than successes
    /// has not earned anything — the strength could be old credit the failures have not yet eaten.
    /// </summary>
    [Fact]
    public void ARouteWithMoreFailuresThanSuccesses_NeverWins() =>
        Assert.Null(RouteGuidedSelection.Prefer(
            new[] { Fit("a", "m1") },
            Trails((Key("coder", "a", "m1"), new TrailView(0.9, 2, 7))),
            "coder"));

    /// <summary>
    /// A TIE KEEPS THE CONFIGURED ANSWER. A guess is beaten only by evidence, never by a different
    /// guess — `.93`'s rule, and the reason `Prefer` compares strictly.
    /// </summary>
    [Fact]
    public void ATieKeepsTheConfiguredRoute()
    {
        var preferred = RouteGuidedSelection.Prefer(
            new[] { Fit("a", "m1"), Fit("b", "m2") },
            Trails(
                (Key("coder", "a", "m1"), new TrailView(0.70, 5, 1)),
                (Key("coder", "b", "m2"), new TrailView(0.70, 5, 1))),
            "coder");

        // The first qualifying candidate holds; the second cannot displace it on an equal number.
        Assert.Equal("a", preferred!.Provider);
    }

    /// <summary>
    /// A TRAIL FOR ANOTHER ROLE IS NOT EVIDENCE ABOUT THIS ONE. The key carries the role precisely
    /// so a model that serves the scribe well cannot promote itself into the coder's seat.
    /// </summary>
    [Fact]
    public void ATrailForAnotherRole_DoesNotApply() =>
        Assert.Null(RouteGuidedSelection.Prefer(
            new[] { Fit("a", "m1") },
            Trails((Key("scribe", "a", "m1"), new TrailView(0.95, 20, 0))),
            "coder"));

    // -------------------------------------------------------------------------------------------
    // Compatibility
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// COMPATIBILITY IS NOT A TIE-BREAK. A route that cannot serve the role is not a weaker option,
    /// it is not an option — however strong its history. A trail cannot make a model able to emit
    /// structured output.
    /// </summary>
    [Fact]
    public void AnIncompatibleRoute_NeverWinsHoweverStrong() =>
        Assert.Null(RouteGuidedSelection.Prefer(
            new[] { Unfit("a", "m1") },
            Trails((Key("coder", "a", "m1"), new TrailView(0.99, 50, 0))),
            "coder"));

    /// <summary>And a compatible weaker route beats an incompatible stronger one.</summary>
    [Fact]
    public void ACompatibleRoute_BeatsAStrongerIncompatibleOne()
    {
        var preferred = RouteGuidedSelection.Prefer(
            new[] { Unfit("a", "strong"), Fit("b", "adequate") },
            Trails(
                (Key("coder", "a", "strong"), new TrailView(0.99, 50, 0)),
                (Key("coder", "b", "adequate"), new TrailView(0.60, 3, 1))),
            "coder");

        Assert.Equal("b", preferred!.Provider);
    }

    // -------------------------------------------------------------------------------------------
    // Authority
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// AN OPERATOR'S EXPLICIT ROUTE IS NEVER REVISITED. This is the clause that keeps learning from
    /// being a second, silent routing policy: a role the operator configured has an answer, and a
    /// trail is not entitled to a second opinion about it. Learning applies only where the role is
    /// being served by the `fallback` entry or the built-in default — a route nobody chose for this
    /// role in particular, which is exactly `.93`'s "a guess, and the only basis a trail may
    /// replace".
    /// </summary>
    [Fact]
    public void AnExplicitlyRoutedRole_IsNotLearnable() =>
        Assert.False(RouteGuidedSelection.IsLearnable(
            roleHasExplicitRoute: true, modelPriorityActive: false));

    /// <summary>
    /// AND A MODEL-PRIORITY OVERRIDE IS AUTHORITY TOO — the loudest instruction the routing surface
    /// has. "I have a better model, use it everywhere" is a decision, and a trail quietly undoing it
    /// for one role would make the setting mean something different per role.
    /// </summary>
    [Fact]
    public void AModelPriorityOverride_IsNotLearnable() =>
        Assert.False(RouteGuidedSelection.IsLearnable(
            roleHasExplicitRoute: false, modelPriorityActive: true));

    /// <summary>Only the unconfigured, unoverridden case learns. Without this the two refusals
    /// above would be satisfied by a rule that never learns at all.</summary>
    [Fact]
    public void AFallbackServedRole_IsLearnable() =>
        Assert.True(RouteGuidedSelection.IsLearnable(
            roleHasExplicitRoute: false, modelPriorityActive: false));

    // -------------------------------------------------------------------------------------------
    // The signal itself
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE VERIFIED SIGNAL IS ITS OWN KIND, and this is the finding the release rests on.
    ///
    /// `model_route` is written per call on `result.Ok`; `verified_route` is written per mission on
    /// `completed_verified`. Same subject, different facts. A routing decision may read only the
    /// second — and it is kept under a different key prefix so the two can never be summed into one
    /// number meaning both.
    /// </summary>
    [Fact]
    public void TheVerifiedRouteSignal_IsDistinctFromThePerCallOne()
    {
        Assert.NotEqual(TrailKind.ModelRoute, TrailKind.VerifiedRoute);
        Assert.True(TrailKind.IsKnown(TrailKind.VerifiedRoute));

        // The per-call trail's key, as ModelRouter writes it, and this one. Different by prefix.
        Assert.StartsWith("verified_route:", Key("coder", "ollama", "m"), StringComparison.Ordinal);
        Assert.DoesNotContain(Key("coder", "ollama", "m"), "model:ollama:m:coder", StringComparison.Ordinal);
    }

    /// <summary>
    /// A VERIFIED MISSION CREDITS THE ROUTES IT USED, and an unverified one does not. Through the
    /// real writer — the same method that credits ant, worker and task-type paths — so the property
    /// is about the site that pays, not about a helper.
    /// </summary>
    [Theory]
    [InlineData(MissionOutcome.CompletedVerified, true)]
    [InlineData(MissionOutcome.CompletedUnverified, false)]
    [InlineData(MissionOutcome.Partial, false)]
    [InlineData(MissionOutcome.WaitingForApproval, false)]
    [InlineData(MissionOutcome.BlockedMissingCapability, false)]
    public void OnlyAVerifiedMission_CreditsItsRoutes(string outcome, bool credited)
    {
        using var memory = Memory();
        var mission = new Anthill.Core.Domain.Mission { Goal = "do the thing" };
        mission.Tasks.Add(new Anthill.Core.Domain.Task
        {
            Title = "work", Description = "work", AssignedAnt = "coder", TaskType = "code_change",
            Status = Anthill.Core.Domain.TaskStatus.Complete, Result = "done",
        });
        memory.SaveMission(mission);

        memory.LogEvent(mission.Id, "model_call", "Model call for role coder: openai/good",
            metadata: new() { ["role"] = "coder", ["provider"] = "openai", ["model"] = "good" });

        memory.UpdateMissionPheromones(mission, outcome);

        var trail = memory.GetPheromoneTrail(Key("coder", "openai", "good"));

        if (!credited)
        {
            // Either nothing was written, or nothing that could qualify. Both mean "this outcome
            // taught the colony nothing about the route" — neither is a promotion.
            Assert.True(trail is null || trail.Strength <= RouteGuidedSelection.BaselineStrength,
                $"'{outcome}' moved the verified-route trail to {trail?.Strength}. Only a verified "
              + "mission is evidence that a route served well.");
            return;
        }

        Assert.NotNull(trail);
        Assert.True(trail!.Strength > RouteGuidedSelection.BaselineStrength,
            $"a verified mission left the route trail at {trail.Strength}, which cannot qualify.");
        Assert.True(trail.SuccessCount > trail.FailureCount);
    }

    /// <summary>
    /// A CHATTY MISSION DOES NOT OUTVOTE A CAREFUL ONE. Forty calls on one route is one relationship
    /// observed forty times; crediting each would let a mission that retried a lot promote its route
    /// over one that got it right first time.
    /// </summary>
    [Fact]
    public void ManyCallsOnOneRoute_CountOnce()
    {
        using var memory = Memory();
        var mission = new Anthill.Core.Domain.Mission { Goal = "chatty" };
        mission.Tasks.Add(new Anthill.Core.Domain.Task
        {
            Title = "work", Description = "work", AssignedAnt = "coder", TaskType = "code_change",
            Status = Anthill.Core.Domain.TaskStatus.Complete, Result = "done",
        });
        memory.SaveMission(mission);

        for (var i = 0; i < 20; i++)
            memory.LogEvent(mission.Id, "model_call", "Model call for role coder: openai/good",
                metadata: new() { ["role"] = "coder", ["provider"] = "openai", ["model"] = "good" });

        memory.UpdateMissionPheromones(mission, MissionOutcome.CompletedVerified);

        var trail = memory.GetPheromoneTrail(Key("coder", "openai", "good"));
        Assert.NotNull(trail);
        Assert.Equal(1, trail!.SuccessCount);
    }
}
