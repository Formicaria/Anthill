using Anthill.Core.Native;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Exercises the hybrid native kernel binding. These pass whether the native C++ library
/// is present (native path) or absent (managed fallback) because both implementations are
/// bit-identical by design — that equivalence is exactly what the migration relies on.
/// </summary>
public class NativeKernelTests
{
    [Fact]
    public void ScoreMission_MatchesPythonContract()
    {
        // 3 of 3 complete, verifier passed -> clamps to 1.00
        Assert.Equal(1.00, NativeKernel.ScoreMission(3, 3, 0, 0, builderBonus: false, verifierVerdict: 1), 2);
        // 4 total, 2 complete, 1 failed, 1 skipped -> 0.5 - 0.25 - 0.05 = 0.20
        Assert.Equal(0.20, NativeKernel.ScoreMission(4, 2, 1, 1, builderBonus: false, verifierVerdict: 2), 2);
        // empty mission -> 0
        Assert.Equal(0.0, NativeKernel.ScoreMission(0, 0, 0, 0, false, 2), 2);
    }

    [Fact]
    public void DetectCycles_FlagsCycleParticipants()
    {
        // 0 -> 1 -> 2 -> 0 (edges express "to depends on from")
        var flags = NativeKernel.DetectCycles(3, new[] { 0, 1, 2 }, new[] { 1, 2, 0 });
        Assert.All(flags, Assert.True);
    }

    [Fact]
    public void DetectCycles_AcyclicGraphFlagsNothing()
    {
        // 0 -> 1, 0 -> 2 : a tree has no cycle
        var flags = NativeKernel.DetectCycles(3, new[] { 0, 0 }, new[] { 1, 2 });
        Assert.All(flags, f => Assert.False(f));
    }

    [Fact]
    public void PheromoneDecay_AppliesFloor()
    {
        var strengths = new[] { 0.8, 0.4, 0.02 };
        var decayed = NativeKernel.PheromoneDecayBatch(strengths, rate: 0.1, floor: 0.05);
        Assert.Equal(0.72, decayed[0], 4);
        Assert.Equal(0.36, decayed[1], 4);
        Assert.Equal(0.05, decayed[2], 4); // floored
    }
}
