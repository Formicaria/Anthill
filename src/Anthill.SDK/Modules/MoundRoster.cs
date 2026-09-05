namespace Anthill.SDK.Modules;

/// <summary>
/// THE SEVEN WORKERS EVERY MICROMOUND RUNS, AND THE ONE PLACE ANTHILL STORES THEM. v0.3.8.123.
///
/// WHY THIS MOVED HERE, OUT OF THE MICROMOUND MODULE. `.122` gave the console a `+ Mound` button
/// that puts a mound chamber in the live colony, drawn with this roster. The roster was served from
/// `/micromound/roster/defaults`, which sits inside the module's `#if MICROMOUND` region — so the
/// chamber's ants arrived only when the micromound repository happened to be checked out beside
/// this one AND the operator held `read_micromound` AND the fleet listing that the fetch was nested
/// inside had already succeeded. Miss any of the three and the operator got a mound chamber with
/// nothing in it, which is what they reported: "i cant see the ants within them at all."
///
/// That is defect class 2 — a feature declared and reaching nobody — and the cause is a
/// PRESENTATION fact being gated behind an INTEGRATION. Seven names on a chamber an operator added
/// to their own colony view are not device data: no mound is contacted to draw them, nothing about
/// them is sent anywhere, and a colony with no micromound repository still has an operator who
/// wants to label their fleet. So the names live where the console can always reach them.
///
/// IT IS STILL ONE STORE, AND STILL CHECKED AGAINST THE DEVICE. `MicromoundRoster.Names` now reads
/// this list rather than declaring its own — the module references this assembly — so there is no
/// second copy to drift. `RosterProjectionTests` continues to compare the module's projection
/// against `Micromound.Runtime.DefaultAnts` by compiled inspection, which means a rename in the
/// device runtime still fails a build here, and it now covers this constant transitively.
///
/// The better home is still upstream: the roster is a documented constant of the architecture
/// rather than a runtime detail, and `DefaultAnts` arguably belongs in `Micromound.Protocol` where
/// both sides could share one declaration. That is a change to the other repository, recorded here
/// rather than made silently.
/// </summary>
public static class MoundRoster
{
    public const string MoundMajor = "Mound Major";
    public const string Scout = "Scout Ant";
    public const string Forager = "Forager Ant";
    public const string Guard = "Guard Ant";
    public const string Witness = "Witness Ant";
    public const string Cache = "Cache Ant";
    public const string Runner = "Runner Ant";

    /// <summary>The seven, in the order ANTS.md draws them: the coordinator, then its six.</summary>
    public static readonly IReadOnlyList<string> Names =
        [MoundMajor, Scout, Forager, Guard, Witness, Cache, Runner];

    /// <summary>
    /// What each one is for, in the words ANTS.md uses. These are RESPONSIBILITIES, not
    /// configuration: the brief is explicit that Anthill "may configure their assignments, allowed
    /// capabilities, applicable routines, limits and relevant options" but "must not silently
    /// change their fundamental role definitions."
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Roles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MoundMajor] = "local coordinator",
            [Scout] = "observation and sensing",
            [Forager] = "requested physical action",
            [Guard] = "runtime health and operational safety",
            [Witness] = "independent physical outcome confirmation",
            [Cache] = "short-term operational persistence",
            [Runner] = "secure external communication",
        };
}
