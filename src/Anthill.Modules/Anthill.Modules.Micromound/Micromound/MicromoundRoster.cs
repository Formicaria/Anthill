using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>One of a mound's workers, as the colony projects it. Not an Anthill ant.</summary>
/// <param name="Name">The worker's name, exactly as the device runtime registers it.</param>
/// <param name="Role">One line, for the colony view and the inspector.</param>
/// <param name="Standard">True for the seven every mound runs; false for a manifest-declared worker.</param>
public sealed record MoundWorker(string Name, string Role, bool Standard)
{
    /// <summary>Capabilities this worker consumes, once a manifest has been read.</summary>
    public IReadOnlyList<string> Consumes { get; init; } = [];

    /// <summary>Capabilities it offers to other workers on the same mound.</summary>
    public IReadOnlyList<string> Exposes { get; init; } = [];

    /// <summary>Its own highest action class, intersected with the charter's on every request.</summary>
    public string ActionCeiling { get; init; } = "observe";
}

/// <summary>
/// THE DEFAULT MICROMOUND COLONY, PROJECTED. v0.3.8.114 — ANTS.md.
///
/// EVERY MOUND RUNS THE SAME SEVEN. A greenhouse, a rover and a workshop differ in hardware and in
/// whatever optional workers their manifest declared; the standard roster is identical across all
/// of them, and specialization happens through capabilities rather than through new ant types.
/// ANTS.md is blunt about it: "a device-specific class in the core (`WateringAnt`,
/// `GreenhouseRuntime`) is the signal an abstraction is wrong."
///
/// THESE ARE NOT ANTHILL ANTS AND MUST NEVER JOIN THE ANTHILL ROSTER. A mound is a child colony
/// attached to this one, so its workers are projected for display and configuration and are never
/// registered as colony workers — seven more rows in `AntRegistry` would make the Queen believe it
/// could dispatch to them, and it cannot: only the Mound Major dispatches inside a mound.
///
/// The names are deliberately distinct from Anthill's own, and ANTS.md says why: "a controller's
/// Verifier judges whether a mission succeeded; a mound's Witness Ant judges whether a valve
/// actually opened. Sharing a name would make two very different questions look like one."
///
/// WHY THIS IS A COPY, AND WHAT KEEPS IT HONEST. The device runtime declares these in
/// `Micromound.Runtime.DefaultAnts`, and this module cannot reference that assembly — it holds the
/// capability kernel, the drivers and the executors, and §33 of the integration brief forbids
/// embedding the Micromound runtime in Anthill for exactly that reason. The wire contract, which we
/// DO reference, does not carry the roster.
///
/// So this is a second store of one fact, which is defect class 5b and would drift. It is made a
/// CHECKED projection instead — and at a better tier than a copy usually allows.
/// `RosterProjectionTests` compares against `DefaultAnts` DIRECTLY: the test project references
/// `Micromound.Sim`, which brings `Micromound.Runtime` with it, so this is compiled inspection
/// rather than a source scan of `Ants.cs`. A rename upstream stops compiling there instead of
/// quietly matching nothing. Only the tests see the runtime; this module still references Protocol
/// and Crypto alone.
///
/// The better fix is upstream — the roster is a documented constant of the architecture rather than
/// a runtime detail, so `DefaultAnts` arguably belongs in `Micromound.Protocol` where both sides
/// could share it. That is a change to the other repository, recorded rather than made silently.
///
/// NOTE THE `using` ABOVE, AND DO NOT REPLACE IT WITH AN INLINE QUALIFIER. This namespace ENDS in
/// `Micromound`, so a qualified `Micromound.Protocol.Foo` written inside it resolves relative to
/// the enclosing namespace and looks for `Anthill.Modules.Micromound.Protocol`, which does not
/// exist. The module README says so; this file learned it the expensive way. Where a fully
/// qualified name is genuinely needed, `global::` is the spelling.
/// </summary>
public static class MicromoundRoster
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

    /// <summary>
    /// The standard colony for a mound, with each worker's capabilities filled in from its manifest
    /// where one has been delivered.
    ///
    /// A mound with no manifest yet still shows all seven, with nothing consumed. That is the
    /// honest rendering: the workers exist the moment the device does — they are the runtime, not a
    /// configuration — and an empty capability list says "not configured yet" where an absent
    /// worker would wrongly say "not present".
    /// </summary>
    public static IReadOnlyList<MoundWorker> For(MoundManifest? manifest)
    {
        var standard = Names.Select(name => new MoundWorker(name, Roles[name], Standard: true)).ToList();

        if (manifest is null) return standard;

        // Optional, manifest-declared workers — ANTS.md's extension point. Data in the manifest,
        // never code in the runtime, and never a replacement for one of the seven: a declared
        // worker whose name collides with a standard one is the manifest trying to redefine the
        // roster, so the standard definition wins and the duplicate is dropped.
        var declared = manifest.Workers
            .Where(w => !Names.Contains(w.Name, StringComparer.Ordinal))
            .Select(w => new MoundWorker(w.Name, w.Purpose, Standard: false)
            {
                Consumes = [.. w.Consumes],
                Exposes = [.. w.Exposes],
                ActionCeiling = w.ActionCeiling,
            });

        return [.. standard, .. declared];
    }
}
