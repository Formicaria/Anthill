using System.Runtime.InteropServices;

namespace Anthill.Core.Native;

/// <summary>
/// Managed binding to the native C++ compute kernel (the hybrid half of v1.8.0).
///
/// On first use it probes for the shared library and verifies the ABI version. If the
/// library is missing or mismatched — for example a pure-managed CI runner — every call
/// transparently falls back to a bit-identical managed implementation, so the colony is
/// never blocked on the native build. <see cref="UsingNative"/> reports which path is live.
/// </summary>
public static class NativeKernel
{
    private const string Lib = "anthill_kernel";
    private const int ExpectedAbi = 1;

    public static bool UsingNative { get; private set; }

    static NativeKernel()
    {
        try
        {
            UsingNative = NativeMethods.anthill_kernel_abi_version() == ExpectedAbi;
        }
        catch (DllNotFoundException) { UsingNative = false; }
        catch (EntryPointNotFoundException) { UsingNative = false; }
        catch (BadImageFormatException) { UsingNative = false; }
    }

    public static double PheromoneUpdate(double currentStrength, double delta, bool success)
    {
        if (UsingNative)
        {
            try { return NativeMethods.anthill_pheromone_update(currentStrength, delta, success ? 1 : 0); }
            catch (Exception) { UsingNative = false; }
        }
        var next = currentStrength + delta + (success ? 0.002 : 0.0);
        return Clamp01(next);
    }

    public static double[] PheromoneDecayBatch(double[] strengths, double rate, double floor)
    {
        if (strengths.Length == 0) return strengths;
        if (UsingNative)
        {
            try
            {
                NativeMethods.anthill_pheromone_decay_batch(strengths, strengths.Length, rate, floor);
                return strengths;
            }
            catch (Exception) { UsingNative = false; }
        }
        var retain = 1.0 - Clamp01(rate);
        var safeFloor = Clamp01(floor);
        for (var i = 0; i < strengths.Length; i++)
        {
            var decayed = strengths[i] * retain;
            strengths[i] = decayed < safeFloor ? safeFloor : Clamp01(decayed);
        }
        return strengths;
    }

    /// <param name="verifierVerdict">-1 failed, 0 needs_improvement, 1 passed, 2 unknown/none.</param>
    public static double ScoreMission(int total, int completed, int failed, int skipped, bool builderBonus, int verifierVerdict)
    {
        if (UsingNative)
        {
            try { return NativeMethods.anthill_score_mission(total, completed, failed, skipped, builderBonus ? 1 : 0, verifierVerdict); }
            catch (Exception) { UsingNative = false; }
        }
        if (total <= 0) return 0.0;
        var score = (double)completed / total - failed * 0.25 - skipped * 0.05;
        if (builderBonus) score += 0.10;
        score += verifierVerdict switch { -1 => -0.25, 0 => -0.10, 1 => 0.05, _ => 0.0 };
        return Clamp01(Math.Round(score, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Flags every node on a dependency cycle. Edges are (from, to) where "to depends on from".
    /// Returns a bool array aligned with node indices.
    /// </summary>
    public static bool[] DetectCycles(int nodeCount, int[] depFrom, int[] depTo)
    {
        var flags = new byte[Math.Max(0, nodeCount)];
        if (nodeCount <= 0) return Array.Empty<bool>();

        if (UsingNative && depFrom.Length == depTo.Length)
        {
            try
            {
                NativeMethods.anthill_detect_cycles(nodeCount, depFrom, depTo, depFrom.Length, flags);
                return flags.Select(b => b != 0).ToArray();
            }
            catch (Exception) { UsingNative = false; }
        }
        return ManagedDetectCycles(nodeCount, depFrom, depTo);
    }

    private static bool[] ManagedDetectCycles(int nodeCount, int[] depFrom, int[] depTo)
    {
        var deps = new List<int>[nodeCount];
        for (var i = 0; i < nodeCount; i++) deps[i] = new List<int>();
        for (var e = 0; e < Math.Min(depFrom.Length, depTo.Length); e++)
        {
            int child = depTo[e], parent = depFrom[e];
            if (child < 0 || child >= nodeCount || parent < 0 || parent >= nodeCount) continue;
            deps[child].Add(parent);
        }

        var state = new byte[nodeCount]; // 0 unvisited, 1 visiting, 2 done
        var inCycle = new bool[nodeCount];
        var path = new List<int>();

        void Visit(int node)
        {
            state[node] = 1;
            path.Add(node);
            foreach (var next in deps[node])
            {
                if (state[next] == 1)
                {
                    var start = path.IndexOf(next);
                    if (start >= 0) for (var k = start; k < path.Count; k++) inCycle[path[k]] = true;
                    else inCycle[next] = true;
                }
                else if (state[next] == 0) Visit(next);
            }
            state[node] = 2;
            path.RemoveAt(path.Count - 1);
        }

        for (var i = 0; i < nodeCount; i++) if (state[i] == 0) Visit(i);
        return inCycle;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static class NativeMethods
    {
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int anthill_kernel_abi_version();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern double anthill_pheromone_update(double currentStrength, double delta, int success);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int anthill_pheromone_decay_batch(double[] strengths, int count, double rate, double floor);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern double anthill_score_mission(int total, int completed, int failed, int skipped, int builderBonus, int verifierVerdict);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int anthill_detect_cycles(int nodeCount, int[] depFrom, int[] depTo, int edgeCount, byte[] inCycle);
    }
}
