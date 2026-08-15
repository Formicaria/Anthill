using Anthill.SDK.Artifacts;

namespace Anthill.Core.Domain;

/// <summary>
/// What each role actually read, produced and proved — rebuilt from artifact IDs alone. v0.3.8.57.
///
/// This is PLAN.md acceptance gate 10: "replaying artifact IDs reconstructs every role's inputs and
/// evidence." The gate could not be attempted before this release because two of the three edges did
/// not exist. Production was recorded from v3.8.19; CONSUMPTION was recorded nowhere, and evidence
/// could not say which revision it judged. Both landed earlier in this release, so the reconstruction
/// is now a query rather than a wish.
///
/// A GATE THAT ONLY SAYS "YES" IS NOT A GATE. The value here is in <see cref="Gaps"/>: the specific
/// ways a replay can be incomplete, each detected and named. An artifact whose payload no longer
/// hashes to what was recorded, a consumption row pointing at an artifact that is gone, evidence
/// citing an artifact the store does not hold — these are the states where a reconstruction would
/// otherwise return something plausible and wrong, which is worse than returning nothing.
///
/// WHAT THIS DOES NOT CLAIM. Roles whose only output is prose — the builder writes the operator
/// answer, and deliberately so — produce no artifacts, so their OUTPUT is not reconstructable and
/// this says so rather than reporting an empty list as if it were a finding. Their INPUTS are, since
/// the consumption ledger records every role that received a context block.
/// </summary>
public sealed record MissionReconstruction
{
    public required string MissionId { get; init; }

    public IReadOnlyList<RoleReconstruction> Roles { get; init; } = Array.Empty<RoleReconstruction>();

    /// <summary>
    /// Every way this replay is incomplete, named. Empty means every referenced artifact is present
    /// and intact, and every piece of evidence is tied to something the store still holds.
    /// </summary>
    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The gate. Deliberately NOT "there are roles" — a mission with no artifacts at all reconstructs
    /// vacuously and would pass a test that only counted roles, which is how a gate comes to certify
    /// nothing. This says the replay contains no CONTRADICTIONS; whether it contains anything is the
    /// caller's question and <see cref="Roles"/> answers it.
    /// </summary>
    public bool IsConsistent => Gaps.Count == 0;

    public static MissionReconstruction For(IArtifactStore artifacts, IEvidenceStore evidence, string missionId)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(evidence);

        var gaps = new List<string>();
        List<Artifact> produced;
        List<ArtifactConsumption> consumed;
        List<Evidence> proofs;

        try
        {
            produced = artifacts.ForMission(missionId).ToList();
            consumed = artifacts.ConsumptionsForMission(missionId).ToList();
            proofs = evidence.ForMission(missionId).ToList();
        }
        catch (Exception error)
        {
            // A reconstruction that throws tells an operator nothing. One that reports it could not
            // read the store tells them exactly what to fix.
            return new MissionReconstruction
            {
                MissionId = missionId,
                Gaps = new[] { $"store_unavailable: {error.Message}" },
            };
        }

        var byId = produced.ToDictionary(a => a.Id, StringComparer.Ordinal);

        // ---- integrity of what is stored -------------------------------------------------------

        foreach (var artifact in produced.Where(a => !a.IsIntact()))
            gaps.Add($"artifact_mutated: {artifact.Id} no longer hashes to the value recorded with it, "
                   + "so every decision that cited it rested on different bytes");

        foreach (var read in consumed)
        {
            if (!byId.TryGetValue(read.ArtifactId, out var artifact))
            {
                gaps.Add($"consumed_artifact_missing: {read.ConsumerRole} read {read.ArtifactId}, "
                       + "which the store no longer holds");
                continue;
            }

            // The reason the consumption ledger records a hash at all. Artifacts are append-only, so
            // this can only fire if that rule was broken — and nothing else in the system would
            // notice, because the artifact still exists and still has the right id.
            if (!read.StillMatches(artifact))
                gaps.Add($"consumed_version_changed: {read.ConsumerRole} read {read.ArtifactId} at "
                       + $"{read.ContentHash}, and it now hashes to {artifact.ContentHash}");
        }

        foreach (var proof in proofs)
        {
            if (proof.ArtifactIds.Count == 0)
            {
                gaps.Add($"evidence_cites_nothing: {proof.Id} ({proof.Kind}) is not attached to any "
                       + "artifact, so a replay cannot say what it was evidence ABOUT");
                continue;
            }

            foreach (var cited in proof.ArtifactIds.Where(id => !byId.ContainsKey(id)))
                gaps.Add($"evidence_artifact_missing: {proof.Id} ({proof.Kind}) cites {cited}, "
                       + "which the store no longer holds");
        }

        // ---- per role ---------------------------------------------------------------------------

        var roles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var a in produced) roles.Add(a.ProducerRole);
        foreach (var c in consumed) roles.Add(c.ConsumerRole);

        var reconstructed = roles.Select(role => new RoleReconstruction
        {
            Role = role,
            // Ordered by when the role first saw it, so a replay reads in the order the work happened.
            ConsumedArtifactIds = consumed
                .Where(c => string.Equals(c.ConsumerRole, role, StringComparison.Ordinal))
                .OrderBy(c => c.FirstReadAt)
                .Select(c => c.ArtifactId).Distinct(StringComparer.Ordinal).ToList(),
            ProducedArtifactIds = produced
                .Where(a => string.Equals(a.ProducerRole, role, StringComparison.Ordinal))
                .OrderBy(a => a.CreatedAt)
                .Select(a => a.Id).ToList(),
            EvidenceIds = proofs
                .Where(e => e.ArtifactIds.Any(id =>
                    byId.TryGetValue(id, out var a) && string.Equals(a.ProducerRole, role, StringComparison.Ordinal)))
                .Select(e => e.Id).ToList(),
        }).ToList();

        return new MissionReconstruction { MissionId = missionId, Roles = reconstructed, Gaps = gaps };
    }
}

/// <summary>One role's replay: what it read, what it made, and what was proved about what it made.</summary>
public sealed record RoleReconstruction
{
    public required string Role { get; init; }

    /// <summary>
    /// What this role ACTUALLY received, from the consumption ledger — not what a task declared it
    /// should receive. A declared input that the budget dropped was never read, and a replay built
    /// from declarations would reconstruct a worker's context as something it never saw.
    /// </summary>
    public IReadOnlyList<string> ConsumedArtifactIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ProducedArtifactIds { get; init; } = Array.Empty<string>();

    /// <summary>Evidence attached to artifacts THIS role produced — what was checked about its work.</summary>
    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when the role's output is recoverable from artifacts. False for a prose-only role such as
    /// the builder — stated rather than left as an empty list, because "produced nothing typed" and
    /// "produced nothing" are different and only one of them is a problem.
    /// </summary>
    public bool OutputIsTyped => ProducedArtifactIds.Count > 0;
}
