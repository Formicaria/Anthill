using Anthill.Core.Configuration;
using Anthill.Core.Workspaces;

namespace Anthill.Core.Tools;

/// <summary>
/// Where a check comes from — ONE decision function, for every caller. v0.3.8.73.
///
/// THE GAP THIS CLOSES. `WorkspaceCapabilityManifest`'s header states the exit gate exactly:
/// "verification commands come from the manifest or operator configuration, never model invention."
/// The manifest half was built in v3.5.0. **The operator half never existed.** There was no way for
/// an operator to say what verifies their workspace, so the answer was always detection — and
/// v0.3.8.71 found the consequence: a workspace the adapters do not recognise has no usable checks
/// at all, because `CheckCatalog.Register` (documented as the "operator/test extension point") is
/// reachable only by naming a check id in task text that `ExecutionService` writes, not the operator.
/// Two qualification scenarios have sat open behind that for four releases.
///
/// PRECEDENCE: OPERATOR, THEN DETECTION, THEN THE COMPILED CATALOG.
///
/// The operator wins because the sentence above names them first and because silently ignoring an
/// explicit configuration is its own defect — the same reasoning the config migration applies to a
/// roster profile ("'full' is an explicit operator choice and is preserved"). Configuring checks
/// REPLACES detection rather than adding to it: an operator whose repository is .NET but who verifies
/// it some other way is stating a fact about their project, and appending `dotnet_build` back onto
/// their list would make the configuration advisory. It is logged at startup for the same reason the
/// roster is — a replacement that is invisible is a replacement nobody can audit.
///
/// WHAT IT IS NOT, and this is the load-bearing part: **not a file inside the workspace.** The
/// checks come from ANTHILL's own configuration, which the colony's missions cannot write — the
/// workspace under modification never contributes. `WorkspaceAdapter`'s doc names the reason:
/// "Keeping those two directions apart is what stops an agent that can edit a repository from
/// editing the thing that checks it." A `.anthill-checks.json` in the repository would have been the
/// convenient design and would have handed every coding agent the power to rewrite its own exam.
/// `PolicyScan.allowlist_tampering` also learned the key, so a patch that proposes editing it is a
/// blocking finding like every other allowlist edit.
///
/// ONE FUNCTION because the alternative was already here and already wrong twice: `TesterAnt` chose
/// what to run with `manifest.IsEmpty ? CheckCatalog.Ids : manifest.Checks`, and
/// `RunAllowlistedCheckTool` resolved what to run with `manifest.Find(id) ?? CheckCatalog.Get(id)`.
/// Those are two spellings of one rule, and the file that holds the second says so: "Two components
/// disagreeing about which catalog is authoritative is how a tester selects an id the runner then
/// refuses." Adding a third source to both by hand would have been a third chance to disagree.
/// </summary>
public static class CheckSource
{
    /// <summary>
    /// Every check available for <paramref name="manifest"/>'s workspace, in precedence order.
    ///
    /// The compiled catalog is last and is a FLOOR rather than a merge: it applies only when neither
    /// the operator nor detection has anything to say, which is the unscoped, unconfigured case the
    /// CLI and the tests run in.
    /// </summary>
    public static IReadOnlyList<CheckDefinition> Available(WorkspaceCapabilityManifest manifest)
    {
        var configured = AnthillRuntime.WorkspaceChecks;
        if (configured.Count > 0) return configured;
        if (!manifest.IsEmpty) return manifest.Checks;
        return CheckCatalog.Ids.Select(CheckCatalog.Get).Where(c => c is not null).Select(c => c!).ToList();
    }

    /// <summary>
    /// Resolve one id against the same precedence. Null when no source declares it, which every
    /// caller must treat as a refusal — an unknown id has no command, and inventing one is the
    /// arbitrary-shell escape the catalog exists to prevent.
    /// </summary>
    public static CheckDefinition? Find(WorkspaceCapabilityManifest manifest, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Available(manifest)
            .FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What the colony would run when a task names no check, given what is available.
    ///
    /// A CONFIGURED or DETECTED workspace runs EVERYTHING it declares, and that is deliberate — the
    /// existing note is right and survives here verbatim in spirit: "a tester that picked a subset
    /// would be choosing which failures the colony is allowed to notice." The `{dotnet_version,
    /// dotnet_build}` pair remains the answer only when nothing is configured and nothing was
    /// detected, because that is the historical behaviour of exactly that case and changing it would
    /// alter what every existing installation verifies.
    /// </summary>
    /// <summary>
    /// The checks that constitute THE BUILD for this workspace. v0.3.8.78 (PLAN.md §2 R2).
    ///
    /// WHY THIS EXISTS. `BuildVerifier` asked the runner for the literal id `dotnet_build`. The
    /// runner has resolved ids through this class since v0.3.8.73 — so a Node or static-frontend
    /// workspace got a correctly-resolved .NET build definition and ran `dotnet build` against a
    /// directory with no project. It failed, deterministically, and a code patch in any non-.NET
    /// workspace could therefore never be verified. The runner was widened and its one caller was
    /// not.
    ///
    /// THE PRECEDENCE IS THE SAME as <see cref="Available"/> and <see cref="DefaultSelection"/>,
    /// deliberately: operator configuration, then what the workspace adapters detected, then the
    /// compiled default. A fourth spelling of this precedence is how the tester and the runner came
    /// to disagree about which catalog was authoritative, which is the defect v0.3.8.73 merged.
    ///
    /// WHAT THE OPERATOR ARM MEANS. If an operator declared checks, those checks ARE the build for
    /// their workspace — all of them, and `BuildVerifier` fails if any fails. This widens WHERE the
    /// check comes from and never whether a reproducible no is final.
    ///
    /// AND THE FALLBACK IS DELIBERATELY NARROWER THAN <see cref="DefaultSelection"/>. That method
    /// returns `dotnet_version` too, which is right for "what could an operator run here" and wrong
    /// here: adding a second command to the build gate would change what verification means for
    /// every existing .NET workspace, in a release about making a non-.NET one work at all. With
    /// nothing declared, this returns exactly what `BuildVerifier` ran before.
    /// </summary>
    public static IReadOnlyList<string> BuildSelection(WorkspaceCapabilityManifest manifest)
    {
        if (AnthillRuntime.WorkspaceChecks.Count > 0)
            return AnthillRuntime.WorkspaceChecks.Select(c => c.Id).ToList();
        if (!manifest.IsEmpty) return manifest.Checks.Select(c => c.Id).ToList();
        return new[] { "dotnet_build" };
    }

    public static IReadOnlyList<string> DefaultSelection(WorkspaceCapabilityManifest manifest)
    {
        if (AnthillRuntime.WorkspaceChecks.Count > 0)
            return AnthillRuntime.WorkspaceChecks.Select(c => c.Id).ToList();
        if (!manifest.IsEmpty) return manifest.Checks.Select(c => c.Id).ToList();
        return new[] { "dotnet_version", "dotnet_build" };
    }

    /// <summary>
    /// Which source answered, for the operator-facing record. A tester reporting PASS is worth
    /// nothing without this: "0 failures" against a check set nobody can identify is the shape of
    /// claim this repository keeps finding.
    /// </summary>
    public static string Describe(WorkspaceCapabilityManifest manifest) =>
        AnthillRuntime.WorkspaceChecks.Count > 0
            ? $"operator configuration ({AnthillRuntime.WorkspaceChecks.Count} declared check(s))"
            : !manifest.IsEmpty
                ? $"workspace detection ({string.Join(", ", manifest.ProjectTypes)})"
                : "the compiled catalog (nothing configured, nothing detected)";
}
