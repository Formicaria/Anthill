namespace Anthill.Core.Configuration;

/// <summary>
/// MISSION REPLAY — THE CONFIGURATION CONTRACT, AND NOTHING ELSE YET.
///
/// Mission Replay will eventually let the colony rerun selected historical tasks described in an
/// external source such as an Obsidian vault. This release establishes only the settings it will be
/// built against: a typed, validated, immutable view over the four `mission_replay_*` keys on
/// <see cref="AnthillConfig"/>. Nothing here reads a vault, parses Markdown, generates a mission,
/// schedules work, touches memory or moves a pheromone.
///
/// WHY A RECORD RATHER THAN FOUR MORE STATIC FIELDS. The neighbouring settings are public static
/// fields that any caller can assign, which is the shape that lets a value be true in one place and
/// false in another. A feature that will eventually be allowed to EXECUTE missions should not be
/// reachable through a field somebody can flip after validation ran. The whole group is therefore
/// one immutable value, replaced wholesale by <c>ProjectConfig</c> and never edited in place.
///
/// WHY VALIDATION RETURNS FINDINGS INSTEAD OF THROWING. `RuntimeConfigValidator` states the house
/// rule plainly: "degrades loudly, never refuses boot — an operator with a half-configured feature
/// needs a running console that explains the problem, not a dead process." A misconfigured vault
/// path is exactly that case, so an operator who enables replay against a directory that is not
/// there gets a console, a startup event and a health finding naming the key — and a feature that
/// reports <see cref="IsOperable"/> false, so nothing downstream can mistake the state for working.
/// The configuration on disk is never rewritten to a default behind their back.
/// </summary>
public sealed record MissionReplayOptions
{
    /// <summary>The tag a note must carry to be considered for replay. Nothing scans for it yet.</summary>
    public const string DefaultReplayTag = "anthill/replay";

    public required bool Enabled { get; init; }

    /// <summary>Root of the operator's vault. Not required to exist while replay is disabled.</summary>
    public required string VaultPath { get; init; }

    public required string ReplayTag { get; init; }

    /// <summary>
    /// Whether VERIFIED replay results may eventually reinforce the existing learning system.
    /// This never means an Obsidian note reaches a pheromone: the intended path is note → replay
    /// mission → normal execution → verification → eligible result → existing learning.
    /// </summary>
    public required bool LearningEnabled { get; init; }

    /// <summary>The safe state, and the value the runtime holds until a config is projected.</summary>
    public static MissionReplayOptions Off { get; } = new()
    {
        Enabled = false,
        VaultPath = "",
        ReplayTag = DefaultReplayTag,
        LearningEnabled = false,
    };

    /// <summary>
    /// Every reason this configuration cannot be honoured as written, each naming the key at fault.
    /// Empty means usable — including the common case of replay simply being off.
    ///
    /// The filesystem is probed ONLY when replay is enabled, because a vault path is allowed to be
    /// stale, absent or on an unmounted drive while the feature is not in use.
    /// </summary>
    public IReadOnlyList<ConfigFinding> Validate()
    {
        var findings = new List<ConfigFinding>();

        // Learning is meaningless without the feature that would produce something to learn from.
        // Reported rather than rejected, and normalised at the point of use by LearningEffective —
        // the operator's file is left exactly as they wrote it.
        if (LearningEnabled && !Enabled)
            findings.Add(new ConfigFinding("warning", "mission_replay_learning_without_replay",
                "mission_replay_learning_enabled is true but mission_replay_enabled is false — "
              + "replay produces no verified results to learn from, so the setting has no effect. "
              + "Enable mission_replay_enabled, or set mission_replay_learning_enabled back to false."));

        if (!Enabled) return findings;

        if (string.IsNullOrWhiteSpace(ReplayTag))
            findings.Add(new ConfigFinding("warning", "mission_replay_without_tag",
                "mission_replay_enabled is true but mission_replay_tag is empty — replay identifies "
              + $"eligible notes by tag, and an empty tag matches nothing. Default: '{DefaultReplayTag}'."));

        if (string.IsNullOrWhiteSpace(VaultPath))
        {
            findings.Add(new ConfigFinding("warning", "mission_replay_without_vault_path",
                "mission_replay_enabled is true but mission_replay_vault_path is empty — there is "
              + "nothing to replay from. Set it to the root directory of your vault."));
            return findings;
        }

        // A path the operating system will not accept is a typo, not a missing directory, and saying
        // so is the difference between "create this folder" and "you have a stray quote in your JSON".
        string full;
        try
        {
            full = Path.GetFullPath(VaultPath.Trim());
        }
        catch (Exception ex)
        {
            findings.Add(new ConfigFinding("warning", "mission_replay_vault_path_unusable",
                $"mission_replay_vault_path ('{VaultPath}') is not a usable path: {ex.Message}"));
            return findings;
        }

        if (File.Exists(full))
            findings.Add(new ConfigFinding("warning", "mission_replay_vault_not_a_directory",
                $"mission_replay_vault_path ('{full}') is a file. It must be the root DIRECTORY of "
              + "the vault."));
        else if (!Directory.Exists(full))
            findings.Add(new ConfigFinding("warning", "mission_replay_vault_missing",
                $"mission_replay_vault_path ('{full}') does not exist. Mission Replay stays inactive "
              + "until it does; nothing else about this installation changes."));

        return findings;
    }

    /// <summary>
    /// Whether replay may operate: switched on AND configured in a way that can be honoured.
    /// Future replay code gates on this, never on <see cref="Enabled"/> alone.
    /// </summary>
    public bool IsOperable => Enabled && Validate().Count == 0;

    /// <summary>
    /// Whether verified replay results may reinforce learning. Deterministically false whenever
    /// replay itself cannot run, so the flag can never outlive the feature it depends on.
    /// </summary>
    public bool LearningEffective => IsOperable && LearningEnabled;

    /// <summary>The absolute vault root, or empty when none is configured. Never throws.</summary>
    public string ResolvedVaultPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(VaultPath)) return "";
            try { return Path.GetFullPath(VaultPath.Trim()); }
            catch { return ""; }
        }
    }
}
