using Anthill.Core.Agents;

namespace Anthill.Core.Planning;

/// <summary>
/// THE PLANNER PROPOSES A SPELLING; THE CONTRACT DECIDES ONE. v0.3.8.133.
///
/// WHAT HAPPENED. An operator asked "how to make tacos". The researcher ran for 22 seconds and
/// produced its brief. Then the verifier refused its own task — `task type 'verify' is outside the
/// verifier execution contract (v1)` — the medic diagnosed it, the builder handed back to the
/// medic, and the mission ended `failed` / `escalated` having answered nothing. The verifier
/// declares `verification`. The plan said `verify`. Nothing in the tree compared the two until the
/// dispatch chokepoint, by which point the work was spent.
///
/// WHY IT SURFACED NOW AND NOT BEFORE. A larger planner model writes the declared spelling most of
/// the time, so the hole stayed closed by luck. A smaller local model — which is the configuration
/// this colony ships for — writes the obvious English word, and every mission it plans dies at its
/// last step. That is the provider-fitness failure mode stated in `docs/PLAN.md` §2e arriving as a
/// concrete outage: nothing was misconfigured, and nothing said so.
///
/// THE RULE. `AntExecutionContract.SupportedTaskTypes` is the authority — it always was, and this
/// type does not add a second one. What it adds is a reconciliation BEFORE dispatch, in the one
/// funnel every planner path already goes through, so a synonym is corrected while the plan is
/// still a plan. Four steps, each refusing to guess further than the last:
///
///   1. The declared type is supported → keep it. The overwhelming case, and it costs one lookup.
///   2. A KNOWN synonym whose target this role actually supports → rewrite. The table below is
///      explicit and small; an alias is never accepted because it looks plausible, only because
///      the contract that will run the task declares its target.
///   3. Otherwise, if the contract declares exactly ONE supported type, that is the only thing the
///      role can do and there is nothing left to choose.
///
/// INFERENCE IS DELIBERATELY NOT ONE OF THE STEPS, and the first cut of this file got that wrong.
/// It fell back to `TextUtil.InferTaskType`, reasoning that an unrecognised spelling should be
/// treated like no spelling — and `InferTaskType` keys on the ROLE, so it answers for every role
/// every time. The refusal below became unreachable: the guard written to prove the gate still
/// fires caught it on the first run. Inference is right for a BLANK type, where the planner said
/// nothing and something must be chosen, and wrong for a stated one, where the planner DID say
/// something and the honest reading of a word no contract knows is disagreement, not absence.
///
/// AND IF NONE OF THAT RESOLVES IT, THE TYPE IS RETURNED UNCHANGED and the dispatch gate refuses
/// as before. A reconciliation that always succeeds is a gate that never does: a plan naming a task
/// type no role in this colony can execute is a real defect, and hiding it behind a default would
/// trade a loud failure for a quiet wrong answer.
/// </summary>
public static class TaskTypeVocabulary
{
    /// <summary>
    /// Spellings a model reaches for, mapped to the declared type they mean. Kept deliberately
    /// small and one-directional: every entry was observed or is the plain-English form of a
    /// declared type, and none of them is accepted unless the ROLE's own contract declares the
    /// target — so `verify` becomes `verification` for a verifier and stays `verify` for a builder,
    /// which is the correct refusal.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // the one that took a mission down
        ["verify"] = "verification",
        ["validate"] = "verification",
        ["validation"] = "verification",
        ["verify_result"] = "verification",
        ["verification_task"] = "verification",
        ["review_result"] = "verification",

        ["test"] = "test_execution",
        ["tests"] = "test_execution",
        ["testing"] = "test_execution",
        ["run_tests"] = "test_execution",
        ["check"] = "validation_check",

        ["build"] = "build_answer",
        ["answer"] = "build_answer",
        ["compose"] = "build_answer",
        ["compose_answer"] = "build_answer",
        ["write_answer"] = "build_answer",
        ["summarize"] = "build_answer",
        ["summary"] = "build_answer",
        ["report"] = "build_answer",

        ["analysis"] = "data_analysis",
        ["analyze"] = "data_analysis",

        ["investigate"] = "research",
        ["research_task"] = "research",
        ["gather"] = "research",
        ["gather_information"] = "research",
        ["search"] = "research",

        ["inspect"] = "file_inspection",
        ["inspect_files"] = "file_inspection",
        ["read_files"] = "file_inspection",
        ["file_read"] = "file_inspection",
        ["file_review"] = "file_inspection",

        ["code"] = "patch_proposal",
        ["implement"] = "patch_proposal",
        ["fix"] = "patch_proposal",
        ["edit_code"] = "patch_proposal",
        ["code_patch"] = "patch_proposal",

        ["diagnose"] = "failure_diagnosis",
        ["diagnosis"] = "failure_diagnosis",
        ["triage"] = "repair_triage",

        ["document"] = "operator_documentation",
        ["docs"] = "operator_documentation",
        ["documentation"] = "operator_documentation",
    };

    /// <summary>
    /// The type this task should carry, given the role that will execute it. Returns the input
    /// unchanged whenever it is already right, whenever the role has no contract to answer with,
    /// and whenever nothing here can honestly decide — see the class remarks for why the last one
    /// is a feature.
    /// </summary>
    public static string Reconcile(string? role, string? declared)
    {
        var type = (declared ?? "").Trim().ToLowerInvariant();
        var contract = AntExecutionCatalog.ContractFor((role ?? "").Trim().ToLowerInvariant());
        if (contract is null || contract.SupportsTaskType(type)) return type;

        if (Aliases.TryGetValue(type, out var alias) && alias is not null && contract.SupportsTaskType(alias))
            return alias;

        if (contract.SupportedTaskTypes.Count == 1) return contract.SupportedTaskTypes.First();

        return type;
    }
}
