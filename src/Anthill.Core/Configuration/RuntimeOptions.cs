using Anthill.Core.Agents;

namespace Anthill.Core.Configuration;

/// <summary>
/// v3.1.0 (ADR-001) — the immutable snapshot of runtime configuration.
///
/// <see cref="AnthillRuntime"/> is a bag of mutable statics. That was the honest .NET translation of
/// the Python module globals it replaced, and it is why two runtimes cannot coexist in one process:
/// every consumer reads whatever the last writer left behind, at whatever moment it happens to look.
/// v2.26.0 found six call sites independently deriving mission success; the same shape of defect is
/// available to any gate that is read twice at two different instants.
///
/// <c>RuntimeOptions</c> is the seam out of that. It is captured ONCE per run from the live statics
/// and then never changes, so everything downstream of the capture point agrees by construction
/// about what the configuration was. New code takes options as a parameter; it does not reach for
/// the static.
///
/// This is deliberately a *migration* type, not a parallel configuration system. It projects the
/// existing <see cref="AnthillConfig"/>/<see cref="AnthillRuntime"/> values verbatim — no new
/// defaults, no new precedence rules, no behaviour of its own. The only thing it adds is the
/// guarantee that a value cannot change underneath a reader.
///
/// Scope note: it carries the fields the MISSION PATH consumes. Homelab, autonomy scheduling, and
/// API-host settings still read the statics; they move behind their own options as later phases
/// reach them. A field is added here when something starts consuming it, never speculatively —
/// an unread option is the declaration-without-a-call-site defect this release exists to remove.
/// </summary>
public sealed record RuntimeOptions
{
    // ---- identity / paths ---------------------------------------------------------------------

    /// <summary>Absolute path of the materialised workspace root.</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>The only directory tree agent file tools may touch.</summary>
    public required string AllowedWorkspaceRoot { get; init; }

    /// <summary>Absolute path to the colony database.</summary>
    public required string DbPath { get; init; }

    /// <summary>Coarse OS+runtime fingerprint a skill's coverage is proven against.</summary>
    public required string EnvironmentFingerprint { get; init; }

    // ---- execution shape ----------------------------------------------------------------------

    public required bool ParallelExecution { get; init; }
    public required int MaxParallelWorkers { get; init; }

    /// <summary>Whole-mission wall-clock budget, in seconds. Becomes an absolute deadline on
    /// <see cref="Orchestration.MissionContext"/> at intake.</summary>
    public required int MaxMissionSeconds { get; init; }

    /// <summary>Per-task wall-clock budget, in seconds.</summary>
    public required int MaxTaskSeconds { get; init; }

    /// <summary>Bounded grace after cancel/timeout for in-flight tasks to observe cancellation.</summary>
    public required int MissionDrainGraceSeconds { get; init; }

    public required bool AutoDependencyWiring { get; init; }

    // ---- capability gates ---------------------------------------------------------------------

    public required bool FileTools { get; init; }
    public required bool FileWriting { get; init; }
    public required bool ShellTool { get; init; }
    public required bool PatchApplication { get; init; }
    public required bool WebSearch { get; init; }

    // ---- mission-path features ------------------------------------------------------------------

    public required bool ModelRouting { get; init; }
    public required bool UseOllama { get; init; }
    public required bool SpecIngestion { get; init; }
    public required bool AnswerSynthesis { get; init; }
    public required bool HandoffIngestion { get; init; }
    public required bool AdaptiveMissionControl { get; init; }
    public required bool ObjectiveVerification { get; init; }
    public required bool SandboxExecution { get; init; }

    // ---- role activation ------------------------------------------------------------------------

    /// <summary>The activation ceiling. Per-role flags apply on top of it.</summary>
    public required ActivationTier ActivationTier { get; init; }

    public required bool SpecialistAntExecution { get; init; }
    public required bool TesterAnt { get; init; }
    public required bool SoldierAnt { get; init; }
    public required bool MedicAnt { get; init; }
    public required bool ArchivistAnt { get; init; }
    public required bool UiCartographerAnt { get; init; }
    public required bool ScribeAnt { get; init; }

    // ---- verification policy inputs ---------------------------------------------------------------

    /// <summary>Break-glass: keep auto-applied changes with no deterministic verification. While
    /// this is on the installation is explicitly not V3-qualifiable.</summary>
    public required bool KeepWithoutVerify { get; init; }

    // ---- research bounds ---------------------------------------------------------------------------

    public required int MaxWebSearchesPerMission { get; init; }
    public required int MaxSourcesPerMission { get; init; }
    public required int MaxContextPacketChars { get; init; }

    /// <summary>
    /// Capture the live runtime into an immutable snapshot. Calls
    /// <see cref="AnthillRuntime.Initialize"/> first (idempotent) so a capture is valid even as the
    /// very first thing a process does.
    ///
    /// This is the ONLY place in v3.1.0 that reads the mission-path statics for options purposes.
    /// Everything downstream takes the snapshot.
    /// </summary>
    public static RuntimeOptions Capture()
    {
        AnthillRuntime.Initialize();
        return new RuntimeOptions
        {
            WorkspaceRoot = AnthillRuntime.WorkspaceRootPath,
            AllowedWorkspaceRoot = AnthillRuntime.AllowedWorkspaceRoot,
            DbPath = AnthillRuntime.DbPath,
            EnvironmentFingerprint = AnthillRuntime.EnvironmentFingerprint,

            ParallelExecution = AnthillRuntime.EnableParallelExecution,
            MaxParallelWorkers = AnthillRuntime.MaxParallelWorkers,
            MaxMissionSeconds = AnthillRuntime.MaxMissionSeconds,
            MaxTaskSeconds = AnthillRuntime.MaxTaskSeconds,
            MissionDrainGraceSeconds = AnthillRuntime.MissionDrainGraceSeconds,
            AutoDependencyWiring = AnthillRuntime.EnableAutoDependencyWiring,

            FileTools = AnthillRuntime.EnableFileTools,
            FileWriting = AnthillRuntime.EnableFileWriting,
            ShellTool = AnthillRuntime.EnableShellTool,
            PatchApplication = AnthillRuntime.EnablePatchApplication,
            WebSearch = AnthillRuntime.EnableWebSearch,

            ModelRouting = AnthillRuntime.EnableModelRouting,
            UseOllama = AnthillRuntime.UseOllama,
            SpecIngestion = AnthillRuntime.EnableSpecIngestion,
            AnswerSynthesis = AnthillRuntime.EnableAnswerSynthesis,
            HandoffIngestion = AnthillRuntime.EnableHandoffIngestion,
            AdaptiveMissionControl = AnthillRuntime.EnableAdaptiveMissionControl,
            ObjectiveVerification = AnthillRuntime.EnableObjectiveVerification,
            SandboxExecution = AnthillRuntime.EnableSandboxExecution,

            ActivationTier = AnthillRuntime.ActivationTier,
            SpecialistAntExecution = AnthillRuntime.EnableSpecialistAntExecution,
            TesterAnt = AnthillRuntime.EnableTesterAnt,
            SoldierAnt = AnthillRuntime.EnableSoldierAnt,
            MedicAnt = AnthillRuntime.EnableMedicAnt,
            ArchivistAnt = AnthillRuntime.EnableArchivistAnt,
            UiCartographerAnt = AnthillRuntime.EnableUiCartographerAnt,
            ScribeAnt = AnthillRuntime.EnableScribeAnt,

            KeepWithoutVerify = AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify,

            MaxWebSearchesPerMission = AnthillRuntime.MaxWebSearchesPerMission,
            MaxSourcesPerMission = AnthillRuntime.MaxSourcesPerMission,
            MaxContextPacketChars = AnthillRuntime.MaxContextPacketChars,
        };
    }
}
