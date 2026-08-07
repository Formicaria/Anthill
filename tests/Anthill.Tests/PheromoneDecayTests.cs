using Anthill.Core.Memory;
using Anthill.Core.Pheromones;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Trails fade with age. v3.8.19 — post-refactor stage 4.
///
/// The colony reinforced trails from v1 and never decayed them, so a trail heavily reinforced months
/// ago stayed exactly as attractive today, and `PrunePheromones` could only reach WEAK trails — a
/// strong-but-stale one was unreachable by any mechanism that existed.
///
/// The function is pure and takes `now` as an argument, which is what makes these tests possible at
/// all: decay at 30, 60 and 300 days is asserted directly rather than approximated by sleeping.
/// </summary>
public class PheromoneDecayTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"anthill-decay-{Guid.NewGuid():N}.db");
    private readonly SqliteMemory _memory;

    public PheromoneDecayTests() => _memory = new SqliteMemory(_dbPath);

    public void Dispose()
    {
        _memory.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static readonly DateTime Now = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---- the arithmetic ---------------------------------------------------

    /// <summary>One half-life moves a trail halfway to neutral. That is the definition.</summary>
    [Fact]
    public void OneHalfLife_MovesHalfwayToNeutral()
    {
        var decayed = PheromoneDecay.Decayed(1.0, Now.AddDays(-30), Now);

        Assert.Equal(0.75, decayed, 3);   // 0.5 + (1.0 - 0.5) * 0.5
    }

    [Fact]
    public void TwoHalfLives_MoveThreeQuartersOfTheWay()
    {
        var decayed = PheromoneDecay.Decayed(1.0, Now.AddDays(-60), Now);

        Assert.Equal(0.625, decayed, 3);
    }

    /// <summary>
    /// The property that makes exponential decay the right shape: strength APPROACHES neutral and
    /// NEVER CROSSES IT. A linear subtraction would pass through 0.5 and keep going, turning age
    /// into evidence of failure — inventing failures that never happened.
    ///
    /// Asserted as `>=` rather than `>`, and the difference is real rather than pedantic. At ten
    /// years the decay factor is 2.4e-37, and `0.5 + 0.5 * 2.4e-37` is exactly 0.5 in double
    /// precision — the remaining distance falls below the epsilon of 0.5, which is about 1.1e-16.
    /// So an ancient trail does not merely approach neutral, it REACHES it. That is the correct
    /// behaviour and the first draft of this test asserted the wrong thing about it: strictly
    /// greater is a claim about floating point, not about the colony.
    /// </summary>
    [Fact]
    public void AVeryOldSuccessNeverBecomesAFailure()
    {
        var decayed = PheromoneDecay.Decayed(1.0, Now.AddDays(-3650), Now);

        Assert.True(decayed >= PheromoneDecay.Neutral, $"decayed to {decayed}, below neutral");
        Assert.True(decayed < 0.51);
    }

    /// <summary>
    /// The same property from the other side, and the one that actually matters: a trail that was
    /// FAILING never drifts up past neutral into looking like a success, however old it gets.
    /// </summary>
    [Fact]
    public void AVeryOldFailureNeverBecomesASuccess()
    {
        var decayed = PheromoneDecay.Decayed(0.0, Now.AddDays(-3650), Now);

        Assert.True(decayed <= PheromoneDecay.Neutral, $"decayed to {decayed}, above neutral");
        Assert.True(decayed > 0.49);
    }

    /// <summary>A weak trail rises toward neutral rather than falling further — decay is forgetting, not punishment.</summary>
    [Fact]
    public void AWeakTrailDecaysUPWARDToNeutral()
    {
        var decayed = PheromoneDecay.Decayed(0.1, Now.AddDays(-30), Now);

        Assert.Equal(0.3, decayed, 3);
        Assert.True(decayed > 0.1);
    }

    [Fact]
    public void AFreshTrailIsUnchanged() =>
        Assert.Equal(0.9, PheromoneDecay.Decayed(0.9, Now, Now), 6);

    /// <summary>
    /// A timestamp in the future is clock skew, not a prediction. Treated as fresh — a negative
    /// exponent would AMPLIFY the trail beyond what any outcome earned it.
    /// </summary>
    [Fact]
    public void AFutureTimestampDoesNotAmplify() =>
        Assert.Equal(0.9, PheromoneDecay.Decayed(0.9, Now.AddDays(30), Now), 6);

    [Fact]
    public void ANeutralTrailStaysNeutral() =>
        Assert.Equal(PheromoneDecay.Neutral, PheromoneDecay.Decayed(PheromoneDecay.Neutral, Now.AddDays(-90), Now), 6);

    // ---- applied to the store ---------------------------------------------

    /// <summary>
    /// Decay must not touch `last_updated`. If it did, the next run would measure age from the decay
    /// rather than from the last real outcome, and a trail decayed nightly would never meaningfully
    /// fade — the mechanism would run forever and do nothing.
    /// </summary>
    [Fact]
    public void DecayingDoesNotCountAsReinforcement()
    {
        _memory.UpdatePheromoneTrail("tool:read_text_file", "tool", success: true, strengthDelta: 0.4);
        var before = _memory.ListPheromoneTrails(50)
            .Single(t => t["trail_key"]?.ToString() == "tool:read_text_file")["last_updated"]?.ToString();

        _memory.DecayPheromones(halfLifeDays: 1, asOf: DateTime.UtcNow.AddDays(10));

        var after = _memory.ListPheromoneTrails(50)
            .Single(t => t["trail_key"]?.ToString() == "tool:read_text_file")["last_updated"]?.ToString();

        Assert.Equal(before, after);
    }

    [Fact]
    public void DecayReportsHowManyTrailsMoved()
    {
        _memory.UpdatePheromoneTrail("tool:a", "tool", success: true, strengthDelta: 0.4);
        _memory.UpdatePheromoneTrail("tool:b", "tool", success: true, strengthDelta: 0.4);

        var moved = _memory.DecayPheromones(halfLifeDays: 1, asOf: DateTime.UtcNow.AddDays(30));

        Assert.Equal(2, moved);
    }

    /// <summary>Running it on a fresh colony changes nothing and says so.</summary>
    [Fact]
    public void DecayingFreshTrailsMovesNothing()
    {
        _memory.UpdatePheromoneTrail("tool:c", "tool", success: true, strengthDelta: 0.4);

        Assert.Equal(0, _memory.DecayPheromones());
    }
}
