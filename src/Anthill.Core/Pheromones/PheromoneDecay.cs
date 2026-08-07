namespace Anthill.Core.Pheromones;

/// <summary>
/// Trails fade. v3.8.19 — post-refactor stage 4, the half that does not need evidence.
///
/// WHAT WAS MISSING. The colony has reinforced pheromone trails since v1: a tool that works gains
/// strength, one that fails loses it. Nothing ever took strength away for AGE. So a trail reinforced
/// heavily in March stayed exactly as attractive in August, and `PrunePheromones` could only remove
/// trails that were WEAK — a strong-but-stale trail was unreachable by any mechanism the colony had.
/// The doc says "weights decay naturally over time"; they did not.
///
/// Why this is safe to build before the artifact store, when reputation and typed pheromones are
/// not: decay reads a TIMESTAMP. It makes no judgement about whether the work was good, so it cannot
/// learn the wrong thing from prose. It is arithmetic over `last_updated`, which has been recorded
/// since the table existed.
///
/// HALF-LIFE, NOT A LINEAR SUBTRACTION. Exponential decay has the property the colony wants: a trail
/// approaches neutrality and never crosses it, so age alone can never turn a success into evidence
/// of failure. A linear rule hits zero and then goes negative, which would invent failures that
/// never happened.
/// </summary>
public static class PheromoneDecay
{
    /// <summary>
    /// Days for an un-reinforced trail to lose half its distance from neutral. Thirty is a
    /// deliberate compromise: short enough that a month-old habit stops steering planning, long
    /// enough that a mission run quarterly still finds its own trail warm.
    /// </summary>
    public const double DefaultHalfLifeDays = 30.0;

    /// <summary>
    /// Trails settle toward this rather than toward zero. Zero would mean "known bad", which is a
    /// different claim from "no longer known", and the colony already spends failure counts on the
    /// first one. 0.5 is the neutral the learning reset uses for the same reason.
    /// </summary>
    public const double Neutral = 0.5;

    /// <summary>
    /// The strength a trail should have now, given what it was and when it was last touched.
    ///
    /// Pure and total: no clock is read here, no row is written. The caller supplies <paramref name="now"/>
    /// so the rule can be tested at any age without waiting, which is the difference between a decay
    /// function that is verified and one that is merely plausible.
    /// </summary>
    public static double Decayed(double strength, DateTime lastUpdated, DateTime now,
                                 double halfLifeDays = DefaultHalfLifeDays)
    {
        if (halfLifeDays <= 0) return strength;

        var ageDays = (now - lastUpdated).TotalDays;
        // A trail from the future is a clock skew, not a prediction. Treat it as fresh rather than
        // amplifying it, which is what a negative exponent would do.
        if (ageDays <= 0) return strength;

        var factor = Math.Pow(0.5, ageDays / halfLifeDays);
        return Neutral + (strength - Neutral) * factor;
    }

    /// <summary>
    /// How much a trail has moved toward neutral. Reported rather than inferred, so a maintenance
    /// run can say what it changed instead of only that it ran.
    /// </summary>
    public static double Drift(double strength, DateTime lastUpdated, DateTime now,
                               double halfLifeDays = DefaultHalfLifeDays) =>
        Math.Abs(strength - Decayed(strength, lastUpdated, now, halfLifeDays));
}
