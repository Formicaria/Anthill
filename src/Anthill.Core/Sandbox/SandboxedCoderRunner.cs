using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Pheromones;
using Anthill.Core.Security;
using Anthill.Core.Tools;

namespace Anthill.Core.Sandbox;

/// <summary>
/// v2.11.0 (NORTH_STAR V3-track Phase 3, wiring) — the first code path that turns the inert
/// v2.10.0 primitives into real iterative work. It wraps a coder's patch proposals in a
/// <see cref="BoundedAgentLoop"/> running inside a disposable <see cref="SandboxWorkspace"/>:
///
///   propose (model)  →  apply IN SANDBOX ONLY  →  run one allowlisted check IN SANDBOX  →
///   inspect result   →  done on green, else feed the failure back for the next bounded turn.
///
/// Safety invariants (all tested):
///  * The gate <see cref="AnthillRuntime.EnableSandboxExecution"/> is checked first — OFF returns a
///    "disabled" report and does no work, so the default install behaves exactly as before.
///  * Writes only ever land inside the sandbox worktree (a <see cref="WorkspacePathGuard"/> rooted
///    at the sandbox refuses traversal); the live checkout is never modified.
///  * Iteration is bounded by the LOOP, not agent judgment — every run ends with an explicable
///    stop reason and can never run unbounded.
///  * Nothing auto-applies. The result is the in-sandbox diff plus the proposals, handed back for
///    the EXISTING approve-then-apply gate. The sandbox is destroyed on dispose.
///
/// The model call is injected (<c>propose</c>) so the runner is deterministic C# and unit-testable
/// without a live model — the loop lifecycle never depends on an LLM.
/// </summary>
public sealed class SandboxedCoderRunner
{
    /// <summary>Produces raw coder patch JSON for a given turn and the feedback from the previous
    /// failed check (empty on the first turn). The caller wires this to the model.</summary>
    private readonly Func<int, string, string> _propose;
    private readonly string _checkId;
    private readonly LoopBudget _budget;
    private readonly bool? _enabledOverride;
    private readonly PatchProposalParser _parser = new();

    /// <param name="enabledOverride">When null (production default) the gate is read from
    /// <see cref="AnthillRuntime.EnableSandboxExecution"/> at run time. Tests pass an explicit value
    /// so they never depend on — or mutate — the shared global gate (which would race across the
    /// parallel test collections).</param>
    public SandboxedCoderRunner(Func<int, string, string> propose, string checkId = "dotnet_build",
        LoopBudget? budget = null, bool? enabledOverride = null)
    {
        _propose = propose ?? throw new ArgumentNullException(nameof(propose));
        _checkId = string.IsNullOrWhiteSpace(checkId) ? "dotnet_build" : checkId;
        _budget = budget ?? new LoopBudget();
        _enabledOverride = enabledOverride;
    }

    public SandboxRunReport Run(string sourceRoot, string missionId, string taskId,
        CancellationToken ct = default, Func<DateTime>? now = null)
    {
        if (!(_enabledOverride ?? AnthillRuntime.EnableSandboxExecution))
            return SandboxRunReport.Disabled(_checkId);

        if (CheckCatalog.Get(_checkId) is null)
            return SandboxRunReport.Refused(_checkId, $"check '{_checkId}' is not in the allowlisted catalog");

        using var sbx = SandboxWorkspace.Create(sourceRoot);
        var guard = new WorkspacePathGuard(sbx.Root);
        var checkTool = new RunAllowlistedCheckTool(sbx.Root);

        var latestProposals = new List<PatchProposal>();
        var lastCheckOutput = "";
        var feedback = "";

        var outcome = BoundedAgentLoop.Run(_budget, turn =>
        {
            var toolCalls = 0;

            // 1. Propose (model). One malformed set shouldn't kill the run; the parser skips bad entries.
            var rawJson = _propose(turn, feedback);
            toolCalls++;
            PatchSet set;
            try { set = _parser.Parse(rawJson, missionId, taskId); }
            catch (Exception e) { return new LoopStep(false, "parse_error", toolCalls, e.Message); }

            latestProposals = set.Proposals;
            if (set.Proposals.Count == 0)
                return new LoopStep(false, "no_proposals", toolCalls, "coder returned no proposals");

            // 2. Apply INTO THE SANDBOX ONLY.
            var (applied, applyErr) = ApplyIntoSandbox(guard, set.Proposals);
            if (!applied)
            {
                feedback = $"The proposed patch could not be applied in the sandbox: {applyErr}. "
                         + "Return a corrected patch (exact old_content for modify; add only for new files).";
                return new LoopStep(false, "apply_failed", toolCalls, applyErr);
            }

            // 3. Run ONE allowlisted check inside the sandbox.
            var result = checkTool.Run(new Dictionary<string, object?> { ["check_id"] = _checkId });
            toolCalls++;
            lastCheckOutput = result.Output;

            // 4. Inspect → done on green, else replan on the failure (bounded by the loop).
            if (result.Success)
                return new LoopStep(true, "verified", toolCalls, "check passed in sandbox");

            feedback = $"The check '{_checkId}' failed in the sandbox. Fix the cause and return a corrected patch.\n"
                     + $"--- check output ---\n{Truncate(result.Output, 4000)}";
            return new LoopStep(false, "check_failed", toolCalls, result.Error ?? "check failed");
        }, ct, now);

        // The diff is the harvestable result — handed back for the existing approval/apply gate.
        return new SandboxRunReport(
            outcome.StopReason, outcome.Turns, outcome.ToolCalls, outcome.Completed,
            sbx.ChangeSummary(), latestProposals, _checkId, lastCheckOutput, outcome.Detail);
        // sbx.Dispose() here: worktree removed + pruned. The live checkout was never touched.
    }

    /// <summary>Deterministic add/modify applier confined to the sandbox root. Delete/rename are
    /// intentionally unsupported here (the sandbox validates additive/modify work; destructive
    /// change types stay a human-reviewed live-apply concern).</summary>
    private static (bool Ok, string Error) ApplyIntoSandbox(WorkspacePathGuard guard, IEnumerable<PatchProposal> proposals)
    {
        foreach (var p in proposals)
        {
            string safe;
            try { safe = guard.ResolveSafePath(p.FilePath); }
            catch (Exception e) { return (false, $"unsafe path '{p.FilePath}': {e.Message}"); }
            if (guard.IsBlockedPath(safe)) return (false, $"refusing blocked path '{p.FilePath}'");

            try
            {
                switch (p.ChangeType)
                {
                    case PatchChangeType.Add:
                        if (string.IsNullOrEmpty(p.NewContent)) return (false, $"add '{p.FilePath}' has empty new_content");
                        Directory.CreateDirectory(Path.GetDirectoryName(safe)!);
                        File.WriteAllText(safe, p.NewContent);
                        break;

                    case PatchChangeType.Modify:
                        if (string.IsNullOrEmpty(p.NewContent)) return (false, $"modify '{p.FilePath}' has empty new_content");
                        if (!File.Exists(safe)) return (false, $"modify target does not exist: '{p.FilePath}'");
                        if (!string.IsNullOrEmpty(p.OldContent))
                        {
                            var current = File.ReadAllText(safe);
                            if (!current.Contains(p.OldContent, StringComparison.Ordinal))
                                return (false, $"old_content not found in '{p.FilePath}'");
                            File.WriteAllText(safe, ReplaceFirst(current, p.OldContent, p.NewContent));
                        }
                        else
                        {
                            File.WriteAllText(safe, p.NewContent);
                        }
                        break;

                    default:
                        return (false, $"sandbox applier supports only add/modify; refusing '{p.ChangeType}' for '{p.FilePath}'");
                }
            }
            catch (Exception e) { return (false, $"apply error on '{p.FilePath}': {e.Message}"); }
        }
        return (true, "");
    }

    private static string ReplaceFirst(string s, string search, string replace)
    {
        var i = s.IndexOf(search, StringComparison.Ordinal);
        return i < 0 ? s : string.Concat(s.AsSpan(0, i), replace, s.AsSpan(i + search.Length));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…(truncated)";
}

/// <summary>Structured, explicable result of one sandboxed coder run. Carries WHY it stopped, the
/// in-sandbox diff, and the proposals to route through the existing approval gate — never applied.</summary>
public sealed record SandboxRunReport(
    string StopReason,                       // disabled | refused | completed | max_turns | max_tool_calls | timeout | repeated_action | cancelled | step_fault
    int Turns,
    int ToolCalls,
    bool Verified,                           // a check passed inside the sandbox
    string ChangeSummary,                    // git diff summary from inside the sandbox
    IReadOnlyList<PatchProposal> Proposals,  // to be approved+applied through the existing gate
    string CheckId,
    string LastCheckOutput,
    string Detail)
{
    public static SandboxRunReport Disabled(string checkId) =>
        new("disabled", 0, 0, false, "(sandbox execution disabled)", Array.Empty<PatchProposal>(), checkId, "",
            "EnableSandboxExecution is false — sandboxed iteration did no work (default-safe).");

    public static SandboxRunReport Refused(string checkId, string reason) =>
        new("refused", 0, 0, false, "(refused before any work)", Array.Empty<PatchProposal>(), checkId, "", reason);
}
