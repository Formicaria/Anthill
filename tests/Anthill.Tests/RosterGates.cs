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

    public static Snapshot Capture() => new(
        AnthillRuntime.EnableSpecialistAntExecution, AnthillRuntime.ActivationTier,
        AnthillRuntime.EnableTesterAnt, AnthillRuntime.EnableSoldierAnt,
        AnthillRuntime.EnableMedicAnt, AnthillRuntime.EnableArchivistAnt,
        AnthillRuntime.EnableUiCartographerAnt, AnthillRuntime.EnableScribeAnt);

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
