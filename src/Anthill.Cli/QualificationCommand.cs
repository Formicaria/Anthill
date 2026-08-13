using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Diagnostics;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Anthill.Core.Outcomes;
using Anthill.Core.Projects;
using Anthill.Modules.Reasoning;
using Anthill.SDK.Artifacts;
using Anthill.SDK.Common;

namespace Anthill.Cli;

/// <summary>
/// <c>anthill --qualification</c> — v0.3.8.53, audit Phase 11.
///
/// Answers the one question an independent tester needs answered before filing any other result:
/// CAN this installation run its supported core mission lifecycle? It runs the full SelfTest
/// battery plus lifecycle-capability checks — patch engine semantics, artifact/evidence store
/// round-trips, finalization idempotency, reasoning availability, role contracts — and exits
/// nonzero when the answer is no.
///
/// SAFE BY CONSTRUCTION: everything runs against a TEMPORARY workspace and a TEMPORARY database
/// created for this run and deleted after it. The operator's colony database, projects and
/// repositories are never opened, let alone modified. The output contains no secrets, tokens or
/// private file contents — versions, check names, availability facts and remediation only.
///
/// Lives in the CLI rather than Core because two checks need the composition root's privileges:
/// naming module types (the agent-CLI scan) and probing the configured local model host. Core
/// must not reference modules; the composition root may (ADR-007).
/// </summary>
public static class QualificationCommand
{
    public static int Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-qual-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        SelfTestReport report;
        try
        {
            using var memory = new SqliteMemory(Path.Combine(dir, "qualification.db"));
            using var queen = new Queen(memory);
            report = SelfTest.Run(queen);
            LifecycleChecks(report, memory, dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine(SelfTest.FormatReport(report));
        Console.WriteLine();
        Console.WriteLine(report.Ok
            ? "QUALIFIED — this installation can run its supported core mission lifecycle."
            : "NOT QUALIFIED — every FAIL line above names its remedy. Fix them and run again.");
        return report.Ok ? 0 : 1;
    }

    private static void LifecycleChecks(SelfTestReport report, SqliteMemory memory, string dir)
    {
        // Repo awareness and the direct-agent lane degrade without git; missions still run.
        // Probed through RepoOps' public surface: Describe of a plain folder reports "unavailable"
        // in its Error exactly when the git binary itself cannot run.
        Check(report, "git_available", warningOnFail: true, () =>
        {
            var state = RepoOps.Describe(dir);
            var missing = state.Error?.Contains("unavailable", StringComparison.OrdinalIgnoreCase) == true;
            return (!missing, missing
                ? "git not found — repo badges, commits and direct-agent capture degrade. Install git."
                : "git answers.");
        });

        // The patch engine's load-bearing semantics, probed pure (Compute does no IO).
        Check(report, "patch_engine_semantics", warningOnFail: false, () =>
        {
            var clean = PatchApply.Compute("modify", "old", "new", "before old after",
                PatchApply.HashOf("before old after"));
            if (clean.Status != PatchApplyStatus.Modified)
                return (false, $"a clean modify computed {clean.Status} — the patch engine cannot apply changes.");
            var stale = PatchApply.Compute("modify", "old", "new", "before old after",
                PatchApply.HashOf("a different base entirely"));
            if (stale.Status != PatchApplyStatus.RefusedStaleBase)
                return (false, $"a stale base computed {stale.Status} — the base-hash guard is not enforced.");
            var ghostDelete = PatchApply.Compute("delete", null, null, currentContent: null);
            if (ghostDelete.Status != PatchApplyStatus.RefusedTargetMissing)
                return (false, $"deleting a missing target computed {ghostDelete.Status} — operation semantics not enforced.");
            return (true, "modify applies; stale base refused; delete of a missing target refused.");
        });

        // The temp workspace is writable — the floor under mission workspaces and materialization.
        Check(report, "workspace_scratch", warningOnFail: false, () =>
        {
            var probe = Path.Combine(dir, "scratch", "probe.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
            File.WriteAllText(probe, "qualification");
            var ok = File.ReadAllText(probe) == "qualification";
            File.Delete(probe);
            return (ok, ok ? "temporary workspace is writable and readable."
                : "could not round-trip a file in the temporary workspace.");
        });

        // Artifact + evidence stores: the persistence the whole evidence graph stands on.
        Check(report, "artifact_evidence_store", warningOnFail: false, () =>
        {
            memory.EnsureSystemMission("__qualification__", "Qualification probe mission");
            var artifact = Artifact.Create("test_report", "tester", "__qualification__", "{\"probe\":true}");
            var artifacts = (IArtifactStore)memory;
            var id = artifacts.Put(artifact);
            var back = artifacts.Get(id);
            if (back is null || back.ContentHash != artifact.ContentHash)
                return (false, "artifact round-trip failed — stored and re-read hashes disagree.");
            var evidence = new Evidence
            {
                Id = $"ev_{Guid.NewGuid():N}", Kind = "hash_match", Deterministic = true, Passed = true,
                ArtifactIds = new[] { id }, Detail = "qualification probe", MissionId = "__qualification__",
            };
            var evidenceStore = (IEvidenceStore)memory;
            evidenceStore.Put(evidence);
            var forMission = evidenceStore.ForMission("__qualification__");
            return (forMission.Count > 0, forMission.Count > 0
                ? "artifact and evidence stores round-trip with intact hashes."
                : "evidence store did not return the probe evidence.");
        });

        // Finalization idempotency: the first claim wins, the replay is refused — the property
        // that stops a crash-replay double-counting pheromones or re-running the archivist.
        Check(report, "finalization_idempotency", warningOnFail: false, () =>
        {
            memory.EnsureSystemMission("__qualification__", "Qualification probe mission");
            var evaluation = new MissionEvaluation("__qualification__", MissionOutcome.CompletedVerified,
                "complete", MissionEvaluation.Verification.Passed, MissionEvaluation.Deliverable.NotApplicable,
                null, "qualification", DateTime.UtcNow.ToString("o"), "qualification probe evaluation");
            var first = MissionFinalizationLedger.TryClaimLearning(memory, "__qualification__", evaluation);
            var replay = MissionFinalizationLedger.TryClaimLearning(memory, "__qualification__", evaluation);
            var ok = first && !replay;
            return (ok, ok ? "first learning claim accepted; the replay was refused."
                : $"first={first} replay={replay} — finalization is not idempotent.");
        });

        // A colony with no way to reason cannot run a mission — the one hard availability gate.
        Check(report, "reasoning_available", warningOnFail: false, () =>
        {
            var agents = AgentCliDiscovery.Scan(force: true)
                .Where(s => s.Installed).Select(s => s.Agent.DisplayName).ToList();
            var ollama = OllamaReachable();
            var ok = agents.Count > 0 || ollama;
            var detail = (agents.Count > 0 ? $"agent CLIs: {string.Join(", ", agents)}" : "no agent CLI installed")
                       + (ollama ? $"; Ollama reachable at {AnthillRuntime.OllamaHost}"
                                 : $"; Ollama not reachable at {AnthillRuntime.OllamaHost}");
            return (ok, ok ? detail
                : detail + " — install an agent from Tools → Integrations, or start Ollama and pull a model.");
        });

        // The twelve mission roles are contracted — structure, not fitness: whether each role's
        // route resolves to a capable model is an environment fact the readiness page reports.
        Check(report, "role_contracts", warningOnFail: false, () =>
        {
            var contracted = AntExecutionCatalog.Contracts.Count;
            return (contracted >= 12, $"{contracted} contracted mission roles registered.");
        });
    }

    private static bool OllamaReachable()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = http.GetAsync(AnthillRuntime.OllamaHost.TrimEnd('/') + "/api/tags")
                .GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Same contract as SelfTest's private Step: never throws, and counts flow to the report.</summary>
    private static void Check(SelfTestReport report, string name, bool warningOnFail, Func<(bool Ok, string Message)> fn)
    {
        string status; string message;
        try
        {
            var (ok, text) = fn();
            status = ok ? "pass" : warningOnFail ? "warning" : "fail";
            message = text;
        }
        catch (Exception error)
        {
            status = warningOnFail ? "warning" : "fail";
            message = $"Check raised: {error.Message}";
        }
        report.Checks.Add(new SelfTestCheck { Name = name, Status = status, Message = message, Details = new() });
        if (status == "pass") report.ChecksPassed++;
        else if (status == "warning") report.ChecksWarning++;
        else { report.ChecksFailed++; report.Ok = false; }
    }
}
