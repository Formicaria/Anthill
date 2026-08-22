using Anthill.Core.Agents;
using Anthill.Core.Configuration;

namespace Anthill.Tests;

/// <summary>
/// Force the roster gates to a known state, and put back exactly what was there. v0.3.8.41.
///
/// WHY THIS HAD TO EXIST. Several gate tests asserted the rollout flags were off by reading the
/// statics directly, which worked for one reason only: the shipped default was `core`, so
/// `ProjectConfig` set them to the same `false` the field initialisers already held, and it made no
/// difference whether configuration had ever been loaded in the test process.
///
/// v0.3.8.41 makes the default `full`. Those same reads now depend on whether some earlier test in
/// the process happened to construct a Queen — which calls `AnthillRuntime.Initialize` — and that is
/// an ordering dependency, not a test. The helpers here make the state explicit at the point it is
/// asserted.
///
/// Restoration is to the PREVIOUS value, never to `false`. The older helpers restored to false,
/// which was indistinguishable from correct while false was also the default and is now a way for
/// one test to silently disable the roster for every test that runs after it.
/// </summary>
internal static class RosterGates
{
    /// <summary>
    /// Run <paramref name="body"/> with every switchable role forced to <paramref name="on"/>.
    ///
    /// The tier is pinned to <see cref="ActivationTier.Full"/> as well, and that is not padding: the
    /// tier is a CEILING applied on top of the flags, so "all flags on" with an ambient tier of
    /// <c>Core</c> still leaves the soldier shut. Leaving it out would make this helper's answer
    /// depend on whatever the last test set — which is the class of bug it exists to remove. When
    /// everything is off the tier is irrelevant, so pinning it is free in that direction too.
    /// </summary>
    public static T WithAll<T>(bool on, Func<T> body) => With(body,
        specialists: on, tier: ActivationTier.Full,
        tester: on, soldier: on, medic: on, archivist: on, uiCartographer: on, scribe: on);

    /// <summary>Run <paramref name="body"/> with the named gates set; anything not named is untouched.</summary>
    public static T With<T>(Func<T> body,
        bool? specialists = null, ActivationTier? tier = null,
        bool? tester = null, bool? soldier = null, bool? medic = null,
        bool? archivist = null, bool? uiCartographer = null, bool? scribe = null)
    {
        var previous = Capture();
        try
        {
            if (specialists is { } s) AnthillRuntime.EnableSpecialistAntExecution = s;
            if (tier is { } t) AnthillRuntime.ActivationTier = t;
            if (tester is { } a) AnthillRuntime.EnableTesterAnt = a;
            if (soldier is { } b) AnthillRuntime.EnableSoldierAnt = b;
            if (medic is { } c) AnthillRuntime.EnableMedicAnt = c;
            if (archivist is { } d) AnthillRuntime.EnableArchivistAnt = d;
            if (uiCartographer is { } e) AnthillRuntime.EnableUiCartographerAnt = e;
            if (scribe is { } f) AnthillRuntime.EnableScribeAnt = f;
            return body();
        }
        finally { Restore(previous); }
    }

    public static void With(Action body,
        bool? specialists = null, ActivationTier? tier = null,
        bool? tester = null, bool? soldier = null, bool? medic = null,
        bool? archivist = null, bool? uiCartographer = null, bool? scribe = null) =>
        With<object?>(() => { body(); return null; },
            specialists, tier, tester, soldier, medic, archivist, uiCartographer, scribe);

    internal sealed record Snapshot(
        bool Specialists, ActivationTier Tier, bool Tester, bool Soldier,
        bool Medic, bool Archivist, bool UiCartographer, bool Scribe);

    /// <summary>
    /// v0.3.8.87 — FORCES THE BOOTSTRAP FIRST, and that one line is what makes every other helper
    /// here reliable.
    ///
    /// This file's header already names the ordering dependency: the flags only mean anything once
    /// `AnthillRuntime.Initialize` has run. What it did not do was close it. `Initialize` is
    /// ONE-SHOT (`if (_initialised &amp;&amp; !force) return;`) and it projects the on-disk config over
    /// every roster flag — and `Queen`'s constructor calls it, then builds the role-availability
    /// snapshot from the result.
    ///
    /// So a test that set the roster and then constructed the FIRST Queen in the process had its
    /// roster silently discarded and ran against the operator's `config.json` instead; the same test
    /// running second kept it, because the bootstrap short-circuits. Whether a test got the colony it
    /// asked for depended on its position in the run, and — because the flags come from a file on the
    /// developer's machine — on whose machine it ran.
    ///
    /// Forcing it here, in the one function every helper below and every fixture above goes through,
    /// fixes all of them at once. Idempotent, so it costs nothing on any later call.
    ///
    /// It does NOT help a test that constructs its Queen before opening the gate: that colony's
    /// snapshot is already taken. Nothing in this file can fix that one — the call has to move.
    /// </summary>
    public static Snapshot Capture()
    {
        AnthillRuntime.Initialize();
        return new(
        AnthillRuntime.EnableSpecialistAntExecution, AnthillRuntime.ActivationTier,
        AnthillRuntime.EnableTesterAnt, AnthillRuntime.EnableSoldierAnt,
        AnthillRuntime.EnableMedicAnt, AnthillRuntime.EnableArchivistAnt,
        AnthillRuntime.EnableUiCartographerAnt, AnthillRuntime.EnableScribeAnt);
    }

    public static void Restore(Snapshot s)
    {
        AnthillRuntime.EnableSpecialistAntExecution = s.Specialists;
        AnthillRuntime.ActivationTier = s.Tier;
        AnthillRuntime.EnableTesterAnt = s.Tester;
        AnthillRuntime.EnableSoldierAnt = s.Soldier;
        AnthillRuntime.EnableMedicAnt = s.Medic;
        AnthillRuntime.EnableArchivistAnt = s.Archivist;
        AnthillRuntime.EnableUiCartographerAnt = s.UiCartographer;
        AnthillRuntime.EnableScribeAnt = s.Scribe;
    }
}
