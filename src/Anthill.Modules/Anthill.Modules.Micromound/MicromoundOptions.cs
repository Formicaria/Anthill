namespace Anthill.Modules.Micromound;

/// <summary>
/// Everything the Micromound module needs from the colony's configuration, handed over rather
/// than read — the same inversion <c>HomelabOptions</c> performs, and for the same reason: a
/// module that reads <c>AnthillRuntime</c> is a module that references the core.
/// </summary>
/// <param name="DatabasePath">Fully resolved by the composition root.</param>
/// <param name="StopFileName">The MICROMOUND_STOP kill switch, mirroring HOMELAB_STOP. Its
/// presence forces a stop order into every sync response, for every mound, with no exceptions and
/// no per-mound override.</param>
/// <param name="WorkspaceRootPath">Where the <c>.anthill</c> directory holding the stop file
/// lives.</param>
/// <param name="ColonyVersion">Advertised at enroll and sync for the version negotiation in
/// PROTOCOL.md §10.</param>
/// <param name="EnrollmentTokenTtlMinutes">How long an operator-minted enrollment token stays
/// usable. Short by design: a token is a one-time secret that becomes a device identity.</param>
/// <param name="MoundOfflineAfterMissedBeats">How many missed sync beats before the fleet widget
/// calls a mound offline. Offline is a normal state, not an incident.</param>
public sealed record MicromoundOptions(
    string DatabasePath,
    string StopFileName,
    string WorkspaceRootPath,
    string ColonyVersion,
    int EnrollmentTokenTtlMinutes = 30,
    int MoundOfflineAfterMissedBeats = 3);
