using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>
/// One mound, weighed against one capability. Carries the reasons either way — a caller that only
/// learns "no" cannot tell an operator whether to plug something in, issue a charter, or clear a
/// stop, and those are three different afternoons.
/// </summary>
/// <param name="MoundId">The device.</param>
/// <param name="Name">Its operator-given label.</param>
/// <param name="Status">online | offline | stopped | quiesced | unenrolled, from the one rule that decides it.</param>
/// <param name="Eligible">Could this mound be asked to do the work, right now, by this origin?</param>
/// <param name="Blockers">Why not. Empty when eligible.</param>
public sealed record MoundCandidate(
    string MoundId, string Name, string Status, bool Eligible, IReadOnlyList<string> Blockers);

/// <summary>
/// WHICH PHYSICAL MOUNDS CAN SATISFY CAPABILITY X — §18. v0.3.8.114.
///
/// A CLEAN SERVICE BOUNDARY, AND NOTHING MORE. The brief is explicit: "This resolver will
/// eventually be usable by Queen. Do not wire Queen into autonomous physical actuation prematurely
/// if Anthill's current intelligence is not ready. Provide the clean service boundary now."
///
/// So this ANSWERS A QUESTION and issues nothing. It has no dependency on missions, it never signs
/// or queues an envelope, and calling it changes nothing about any mound. The Queen can consult it
/// today to find out whether physical work is even possible, and the answer costs nothing if it
/// then does not ask — which is what makes it safe to expose before the autonomy it serves exists.
///
/// EVERY CONDITION §18 NAMES IS CHECKED, and each reads from the one place that owns it rather than
/// being re-derived here:
///
///   capability   — the manifest the colony authored, falling back to what the device reported
///   routine      — the same manifest, since a charter may only enable routines that exist
///   online state — `MicromoundWidgets.StatusOf`, the single rule the fleet widget also uses
///   stop state   — `MicromoundStop`, file and record together
///   lease        — `MicromoundCharters.LeaseExpired`, read from what the colony granted
///   charter      — the charter on file, and whether it actually grants this capability
///   policy       — `MicromoundAutonomy`, evaluated for the ASKING origin
///
/// THE POLICY CHECK IS WHY ORIGIN IS A PARAMETER. "Which mounds can water the greenhouse" has a
/// different answer for an operator than for the Queen, and returning the operator's answer to the
/// Queen would be a resolver that promises capacity the dispatcher then refuses. A resolver whose
/// answers the next gate rejects is worse than no resolver — it moves the refusal later, where it
/// looks like a malfunction rather than a policy.
/// </summary>
public sealed class MicromoundResolver(IMoundStore store)
{
    private readonly IMoundStore _store = store;

    /// <summary>
    /// Every mound, ordered eligible-first, weighed against one capability or routine id.
    ///
    /// Returns ALL of them rather than only the eligible ones, deliberately. "No mound can do this"
    /// and "one mound could, but its lease lapsed" are different answers, and a filtered list
    /// collapses them into the same empty result.
    /// </summary>
    public IReadOnlyList<MoundCandidate> Resolve(string capabilityOrRoutine, PhysicalOrigin origin,
        DateTimeOffset now)
    {
        var options = MicromoundRuntime.Options;
        var globalStop = MicromoundStop.IsEngaged(options);

        return [.. _store.ListMounds()
            .Select(mound => Weigh(mound, capabilityOrRoutine, origin, now, options, globalStop))
            .OrderByDescending(c => c.Eligible)
            .ThenBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>The eligible ones only — for a caller that has already decided what to do with none.</summary>
    public IReadOnlyList<MoundCandidate> Eligible(string capabilityOrRoutine, PhysicalOrigin origin,
        DateTimeOffset now) =>
        [.. Resolve(capabilityOrRoutine, origin, now).Where(c => c.Eligible)];

    private MoundCandidate Weigh(MoundRecord mound, string wanted, PhysicalOrigin origin,
        DateTimeOffset now, MicromoundOptions options, bool globalStop)
    {
        var blockers = new List<string>();
        var status = MicromoundWidgets.StatusOf(mound, options, now, globalStop);

        // WHAT THE MOUND PHYSICALLY HAS. The manifest is the colony's own authored view; the
        // device's reported capability list is the fallback for a mound enrolled but not yet
        // configured. Neither is a grant — the charter below is.
        var manifest = string.IsNullOrEmpty(mound.ManifestId) ? null : _store.GetManifest(mound.ManifestId);
        var isRoutine = CapabilityId.IsRoutine(wanted);

        var present = isRoutine
            ? manifest?.Routines ?? []
            : (IReadOnlyList<string>)(manifest?.Capabilities ?? mound.Capabilities);

        if (!present.Contains(wanted, StringComparer.Ordinal))
            blockers.Add(isRoutine
                ? $"does not register routine '{wanted}'"
                : $"does not have capability '{wanted}'");

        if (status is "stopped") blockers.Add("a stop is in force");
        if (status is "unenrolled") blockers.Add("not enrolled");
        if (status is "offline") blockers.Add("offline — last seen " + (string.IsNullOrEmpty(mound.LastSeen)
            ? "never" : mound.LastSeen));

        // A CHARTER IS THE GRANT, and a mound holding none is observe-only by definition.
        var charter = string.IsNullOrEmpty(mound.CharterId) ? null : _store.GetCharter(mound.CharterId);

        if (charter is null)
        {
            blockers.Add("holds no charter, so it may only observe");
        }
        else
        {
            var granted = isRoutine ? charter.Routines : charter.Capabilities;
            if (!granted.Contains(wanted, StringComparer.Ordinal))
                blockers.Add($"its charter does not grant '{wanted}'");

            if (MicromoundCharters.LeaseExpired(mound, now))
                blockers.Add("its lease has expired; it needs fresh authority rather than a reconnection");
        }

        // AND WHETHER THIS ASKER MAY SPEND IT. Evaluated against the charter's ceiling, which is the
        // outermost bound the colony knows — the mound intersects it further, downward only.
        if (charter is not null && ActionClasses.TryParse(charter.ActionCeiling, out var ceiling))
        {
            var verdict = MicromoundAutonomy.Evaluate(
                mound.AutonomyPolicy, origin, ceiling, globalStop || mound.Stopped);

            // An approval being owed is NOT a blocker. The mound can do the work and the colony is
            // willing to ask — it simply asks a person first. Reporting it as ineligible would hide
            // every mound an operator could authorise in one click.
            if (!verdict.Allowed) blockers.Add(verdict.Reason);
        }

        return new MoundCandidate(mound.MoundId, mound.Name, status, blockers.Count == 0, blockers);
    }
}
