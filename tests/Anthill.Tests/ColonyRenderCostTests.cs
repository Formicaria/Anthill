using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// WHAT A GRAIN COSTS PER FRAME. v0.3.9.7.
///
/// The operator's question was whether the live view struggled because there were too many
/// particles. It was not the count. A chamber holding the whole vault drew fifteen thousand records,
/// and each one cost far more than a one-pixel disc has any business costing:
///
///   1. `proj` called `Math.cos` and `Math.sin` on the yaw AND the pitch INSIDE the per-point
///      projection — four trig calls per point per frame, all computing the same four numbers,
///      because the camera does not move within a frame. At 15,000 points and 60fps that is about
///      3.6 million redundant trig calls a second. The cache already existed: `lightPrep()` has
///      stored exactly those four values once per frame since the lighting was written, and
///      `shadeAt` has read them the whole time. `proj` simply never did.
///
///   2. The grain branch built `'rgba(r,g,b,a)'` by string concatenation per point and assigned it
///      to `fillStyle`, so the canvas parsed fifteen thousand CSS colour strings a frame.
///
///   3. Each grain took its own `beginPath`/`arc`/`fill`.
///
/// None of the three is the dots being numerous. All three are the dots being drawn ONE AT A TIME.
///
/// THESE ARE SOURCE GUARDS, and that is a deliberate choice rather than a convenience. A frame-rate
/// assertion needs a canvas, a GPU and a machine whose load nobody controls, and it would fail for
/// reasons that have nothing to do with this code — which is how a performance test comes to be
/// disabled and then deleted. What is actually being protected is three specific shapes, each of
/// which reads as perfectly correct code: a self-contained projection, a colour built where it is
/// used, a fill next to its path. None of them looks like a defect, which is exactly why they need
/// something other than review to keep them out.
/// </summary>
public class ColonyRenderCostTests
{
    private static string Live() => SourceText.CodeOnly(File.ReadAllText(
        Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", "colony-live.js")));

    /// <summary>
    /// The camera basis is computed once per frame, not once per point.
    /// </summary>
    [Fact]
    public void Projection_DoesNotRecomputeTheCameraBasisPerPoint()
    {
        var live = Live();

        var at = live.IndexOf("function proj(", StringComparison.Ordinal);
        Assert.True(at > 0, "colony-live.js has no `proj` function; the guard is pointed at nothing.");

        var body = SourceText.MemberBody(live, at);

        Assert.False(Regex.IsMatch(body, @"Math\.(cos|sin)\s*\("),
            "`proj` computes a trig function again. It runs once PER POINT PER FRAME — fifteen "
          + "thousand times in a vault chamber — and the camera does not change within a frame. "
          + "`lightPrep()` already caches the yaw and pitch sine/cosine in LT once per frame; read "
          + "those. This was the largest single cost in the frame before v0.3.9.7.");

        // And it is reading the cache, rather than having had the trig removed some other way.
        Assert.Contains("LT.cyw", body, StringComparison.Ordinal);
        Assert.Contains("LT.sp", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Grains are drawn in batches keyed by colour, not one fill at a time.
    /// </summary>
    [Fact]
    public void Grains_AreBatchedByColour_RatherThanFilledIndividually()
    {
        var live = Live();

        Assert.Contains("function flushGrains()", live, StringComparison.Ordinal);
        Assert.Contains("grainBuckets", live, StringComparison.Ordinal);

        /* THE GRAIN BRANCH PUSHES; IT DOES NOT FILL. Asserted as the exact push rather than as the
           absence of a `fillStyle` assignment, because an ANT legitimately sets one — it is a halo,
           a core and a ring, and there are a dozen of them, not fifteen thousand. A negative regex
           broad enough to catch the grain would also catch the ant. */
        Assert.Contains("(grainBuckets[key] || (grainBuckets[key] = [])).push(q.x, q.y, rad);",
            live, StringComparison.Ordinal);

        // And the key is QUANTISED, or every grain gets its own bucket and the batching buys nothing.
        Assert.Contains("Math.round(alpha * 20) / 20", live, StringComparison.Ordinal);
        Assert.Contains("Math.round((p.coreMix || 0) * 8) / 8", live, StringComparison.Ordinal);

        /* THE FLUSH HAPPENS IN MORE THAN ONE PLACE, and every one of them is a layering rule rather
           than a repetition: before the first resident (ants paint over grains, which is what the
           point order has always meant), before a tier-3 label, before a hover ring, and once after
           the loop for a chamber that has no residents to trigger the lazy flush. Four call sites
           plus the definition. Fewer than that means one of those layers is now underneath the
           grains — which is a rendering bug that looks like a rendering choice. */
        var calls = Regex.Matches(live, @"flushGrains\(\)").Count;
        Assert.True(calls >= 5,
            $"`flushGrains` appears {calls} times. It must run before residents, before a label, "
          + "before a hover ring and once after the loop — drop one and that layer draws under the "
          + "grains instead of over them.");
    }

    /// <summary>
    /// A chamber too far away to resolve individual grains draws a STABLE sample of them.
    ///
    /// Stable is the whole property. A random sample, or one chosen by distance, changes membership
    /// between frames and the chamber crawls; choosing by index does not move, and because the point
    /// array is ordered by cluster a stride crosses every folder evenly rather than dropping one.
    /// </summary>
    [Fact]
    public void AnUnfocusedChamber_SamplesItsGrains_ByAStableIndexStride()
    {
        var live = Live();

        Assert.Contains("lodStride", live, StringComparison.Ordinal);
        Assert.Contains("!isFocused && s.pts.length >", live, StringComparison.Ordinal);

        // Index, not randomness or distance: those shimmer.
        Assert.Contains("pi % lodStride", live, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.random() < lodStride", live);

        /* A SKIPPED POINT CLEARS ITS `_q`. The picker reads `_q` to decide what the cursor is over,
           so a stale one makes a grain clickable where it USED to be — the class of bug that gets
           reported as "clicking does nothing" and is nearly impossible to reproduce deliberately. */
        Assert.Contains("pi % lodStride !== 0", live, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"pi % lodStride !== 0[\s\S]{0,200}_q = null"), live);
    }
}
