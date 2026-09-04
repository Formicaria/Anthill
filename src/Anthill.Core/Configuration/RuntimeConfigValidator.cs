using Anthill.Core.Agents;

namespace Anthill.Core.Configuration;

/// <summary>
/// v2.26.0 pre-V3 hardening — configuration health. Adaptive features and specialist roles can be
/// enabled in combinations that cannot work (adaptive repair with Medic disabled; handoff
/// ingestion with no executable destinations; auto-apply with no deterministic verification).
/// Before this, such a configuration ran silently and simply did nothing — the runtime CLAIMED
/// the feature was active while its dependency was off.
///
/// The validator degrades loudly, never refuses boot: an operator with a half-configured feature
/// needs a running console that explains the problem, not a dead process. Findings surface at
/// startup (events) and live at /config/health and /colony/introspection.
/// </summary>
public sealed record ConfigFinding(string Severity, string Combination, string Detail);

public static class RuntimeConfigValidator
{
    public static List<ConfigFinding> Validate()
    {
        var findings = new List<ConfigFinding>();
        void Add(string severity, string combination, string detail) =>
            findings.Add(new ConfigFinding(severity, combination, detail));

        // Adaptive repair depends on the Medic specialist actually being executable.
        if (AnthillRuntime.EnableHandoffIngestion && !AnthillRuntime.EnableMedicAnt
            && AnthillRuntime.ActivationTier != ActivationTier.Core)
            Add("warning", "adaptive_repair_without_medic",
                "Handoff ingestion / adaptive repair is enabled but the Medic specialist is disabled — "
                + "repair decisions will be made and then have no executor to route to.");

        // Handoff ingestion with no executable specialist destinations is a no-op wearing an ON switch.
        if (AnthillRuntime.EnableHandoffIngestion
            && !AnthillRuntime.EnableTesterAnt && !AnthillRuntime.EnableMedicAnt
            && !AnthillRuntime.EnableSoldierAnt && !AnthillRuntime.EnableArchivistAnt)
            Add("warning", "handoff_ingestion_without_destinations",
                "Handoff ingestion is enabled but every specialist destination is disabled — "
                + "every proposed handoff will be rejected at admission.");

        // Auto-apply without objective verification: changes land against a weaker success gate.
        if (AnthillRuntime.AutonomyAutoApplyEnabled && !AnthillRuntime.EnableObjectiveVerification)
            Add("warning", "auto_apply_without_objective_verification",
                "Auto-apply is enabled but objective (deliverable) verification is disabled — "
                + "the canonical evaluation cannot check that the goal's deliverable was produced "
                + "before its patches are applied.");

        // Auto-apply with the break-glass keep-without-verify option: the installation is
        // explicitly unqualified while this is on.
        if (AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify)
            Add("critical", "break_glass_keep_without_verify",
                "autonomy_autoapply_keep_without_verify is ON. Autonomous changes can be kept "
                + "without deterministic verification. This is a development break-glass option: "
                + "the installation is NOT V3-qualifiable while it is enabled, kept changes never "
                + "record verified success, and never reinforce learning.");

        // Auto-apply write path without the write gates is inert.
        if (AnthillRuntime.AutonomyAutoApplyEnabled
            && (!AnthillRuntime.EnablePatchApplication || !AnthillRuntime.EnableFileWriting))
            Add("warning", "auto_apply_without_write_gates",
                "Auto-apply is enabled but patch_application_enabled/file_writing_enabled are off — "
                + "nothing will ever be applied.");

        // Mission Replay's own findings, which the options type owns because the rules are about
        // one feature's four keys rather than a combination across features.
        findings.AddRange(AnthillRuntime.MissionReplay.Validate());

        // Sandbox execution needs a usable workspace root.
        if (AnthillRuntime.EnableSandboxExecution
            && (string.IsNullOrWhiteSpace(AnthillRuntime.AllowedWorkspaceRoot)
                || !Directory.Exists(AnthillRuntime.AllowedWorkspaceRoot)))
            Add("warning", "sandbox_without_workspace",
                $"Sandbox execution is enabled but the workspace root "
                + $"('{AnthillRuntime.AllowedWorkspaceRoot}') does not exist — every sandbox run will fall back.");

        return findings;
    }

    /// <summary>Log every finding as a startup event — degraded LOUDLY, never silently.</summary>
    public static void ReportAtStartup(Memory.SqliteMemory memory)
    {
        foreach (var f in Validate())
            memory.LogEvent(AnthillRuntime.SystemApiMissionId, "config_health_finding",
                $"[{f.Severity}] {f.Combination}: {f.Detail}", antName: "config",
                metadata: new() { ["severity"] = f.Severity, ["combination"] = f.Combination });
    }
}
