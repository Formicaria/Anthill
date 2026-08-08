using Anthill.SDK.Artifacts;

namespace Anthill.Core.Outcomes;

/// <summary>
/// The verdict a mission's STORED EVIDENCE supports, computed without asking anything. v3.8.27.
///
/// The colony's founding rule is that only reproducible evidence may carry a mission to a verified
/// outcome. Everything since v3.8.19 has been building the evidence to enforce it — the store, the
/// producers, deterministic verifiers running in a workspace that actually contains the patch — and
/// the last consumer was still a model: <c>VerifierAnt</c> asks one for prose and
/// <c>VerificationVerdict.Parse</c> reads the words "verification passed" back out of it.
///
/// That is the last place model output reaches a verification decision. This is what replaces it.
///
/// The rule, in order:
///
///   1. Any DETERMINISTIC evidence that FAILED   -> failed. A compiler said no; nothing outvotes it.
///   2. At least one DETERMINISTIC evidence PASSED and none failed -> passed.
///   3. Deterministic evidence exists but is inconclusive -> needs_improvement.
///   4. NO deterministic evidence at all -> unknown. Not "passed" — the absence of proof is not proof,
///      and a mission with only model reviews behind it has not been verified by anything.
///
/// Non-deterministic evidence is READ and REPORTED and never decides. A model review that says the
/// patch is excellent cannot move any of the four branches above, which is the whole point.
/// </summary>
public static class EvidenceVerdict
{
    /// <summary>What the evidence supports, and why — the explanation is for the operator, and is
    /// never parsed by anything.</summary>
    public sealed record Result(
        string Verdict,
        int DeterministicPassed,
        int DeterministicFailed,
        int NonDeterministicRecorded,
        string Explanation)
    {
        /// <summary>True only for an explicit deterministic pass. `unknown` is not a pass.</summary>
        public bool IsPass => Verdict == VerificationVerdict.Passed;

        /// <summary>Whether ANY reproducible check ran. The difference between "we checked and it
        /// was fine" and "nobody checked", which the old prose verdict could not express.</summary>
        public bool HasDeterministicEvidence => DeterministicPassed + DeterministicFailed > 0;
    }

    /// <summary>
    /// Compute the verdict from a mission's evidence rows.
    /// </summary>
    /// <param name="evidence">Everything recorded for the mission. Order does not matter; a single
    /// deterministic failure decides regardless of what came after it.</param>
    public static Result For(IReadOnlyList<Evidence> evidence)
    {
        if (evidence is null || evidence.Count == 0)
            return new(VerificationVerdict.Unknown, 0, 0, 0,
                "no evidence was recorded for this mission — nothing has been verified");

        var deterministic = evidence.Where(e => e.Deterministic).ToList();
        var passed = deterministic.Count(e => e.Passed);
        var failed = deterministic.Count(e => !e.Passed);
        var semantic = evidence.Count - deterministic.Count;

        if (failed > 0)
        {
            var names = string.Join(", ", deterministic.Where(e => !e.Passed)
                .Select(e => e.Kind).Distinct().OrderBy(k => k, StringComparer.Ordinal));
            return new(VerificationVerdict.Failed, passed, failed, semantic,
                $"{failed} reproducible check(s) failed ({names}) — a deterministic failure is not "
                + "overridable by review");
        }

        if (passed > 0)
            return new(VerificationVerdict.Passed, passed, failed, semantic,
                $"{passed} reproducible check(s) passed and none failed"
                + (semantic > 0 ? $"; {semantic} non-deterministic review(s) recorded but not counted" : ""));

        // Evidence exists, and none of it is reproducible. This is the case that used to read as a
        // pass whenever a model wrote the right words.
        return new(VerificationVerdict.Unknown, 0, 0, semantic,
            $"{semantic} non-deterministic review(s) recorded and NO reproducible check — "
            + "semantic judgment alone cannot verify a mission");
    }
}
