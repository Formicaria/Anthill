using Anthill.Core.Common;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;

namespace Anthill.Core.Orchestration;

/// <summary>
/// v3.1.0 (ADR-001) — what the operator reads.
///
/// A finished mission carries three parallel accounts of itself, and keeping them straight is the
/// whole job here:
///
/// <list type="bullet">
/// <item><c>UserResult</c> — the raw output of the best completed task. Never rewritten.</item>
/// <item><c>DebugResult</c> — the full per-task trace. Never truncated in storage.</item>
/// <item><c>FinalResult</c> — a plain-English answer synthesised from the raw one. What is shown
/// FIRST, and the only one a model ever touches.</item>
/// </list>
///
/// The rule that governs all of it: synthesis is a presentation nicety and must never be able to
/// leave a finished mission answerless. Every failure path — no router, feature off, answer too
/// short, provider down, empty or <c>ERROR:</c> response, an exception — falls back to the raw
/// answer. The decision rules are pure static functions precisely so they can be proven without a
/// live provider.
/// </summary>
public interface IResultAssembler
{
    /// <summary>
    /// Populate the mission's operator-facing fields. Called once at finalization, after grading
    /// and learning, before the mission is saved.
    /// </summary>
    void Assemble(Mission mission, MissionContext context);

    /// <summary>The console/CLI rendering of a finished mission.</summary>
    string ComposeCliResult(Mission mission);
}

public sealed class ResultAssembler : IResultAssembler
{
    private readonly SqliteMemory _memory;
    private readonly ModelRouter? _router;

    public ResultAssembler(SqliteMemory memory, ModelRouter? router)
    {
        _memory = memory;
        _router = router;
    }

    public void Assemble(Mission mission, MissionContext context)
    {
        mission.BestOutputTaskId = SelectBestOutputTaskId(mission);
        mission.UserResult = ComposeUserResult(mission);
        mission.DebugResult = ComposeDebugResult(mission);
        // v2.16.0: FinalResult is what the operator reads. UserResult (raw best task) and
        // DebugResult (full trace) are untouched, so the detail behind the answer is always there.
        mission.FinalResult = ComposeFinalAnswer(mission, context);
    }

    // ---- selection -------------------------------------------------------------------------------

    /// <summary>
    /// The task whose output best answers the mission: the last completed builder, else the last
    /// completed coder, else the last completed task with any result at all.
    /// </summary>
    public static string? SelectBestOutputTaskId(Mission mission)
    {
        var builder = mission.Tasks.LastOrDefault(t => t.AssignedAnt == "builder" && t.Status == TaskStatus.Complete && !string.IsNullOrEmpty(t.Result));
        if (builder is not null) return builder.Id;
        var coder = mission.Tasks.LastOrDefault(t => t.AssignedAnt == "coder" && t.Status == TaskStatus.Complete && !string.IsNullOrEmpty(t.Result));
        if (coder is not null) return coder.Id;
        var completed = mission.Tasks.LastOrDefault(t => t.Status == TaskStatus.Complete && !string.IsNullOrEmpty(t.Result));
        return completed?.Id;
    }

    public static string ComposeUserResult(Mission mission)
    {
        if (mission.BestOutputTaskId is not null)
        {
            var best = mission.Tasks.FirstOrDefault(t => t.Id == mission.BestOutputTaskId && !string.IsNullOrEmpty(t.Result));
            if (best is not null) return best.Result!;
        }
        var fallbackId = SelectBestOutputTaskId(mission);
        if (fallbackId is not null)
        {
            var task = mission.Tasks.FirstOrDefault(t => t.Id == fallbackId && !string.IsNullOrEmpty(t.Result));
            if (task is not null) return task.Result!;
        }
        return "Mission produced no completed user-facing output.";
    }

    public static string ComposeDebugResult(Mission mission) => string.Join("\n", mission.Tasks.Select(t =>
        $"Task: {t.Title}\nTask ID: {t.Id}\nAnt: {t.AssignedAnt}\nTask Type: {t.TaskType}\nDepends On: [{string.Join(", ", t.DependsOn)}]\n" +
        $"Parent Task IDs: [{string.Join(", ", t.ParentTaskIds)}]\nStatus: {t.Status.Value()}\nResult Chars: {t.ResultChars}\n" +
        $"Estimated Tokens: {t.EstimatedTokens}\nResult Summary:\n{t.ResultSummary}\n\nFull Result:\n{t.Result}\n"));

    // ---- answer synthesis ------------------------------------------------------------------------

    /// <summary>
    /// v2.16.0: the length below which synthesis is skipped.
    ///
    /// A short answer is usually already a sentence or two of prose, and paying a model call to
    /// rewrite it buys nothing. Anything longer is where raw task output starts being a dump —
    /// JSON, a diff, a file listing — which is what the operator actually wanted rewritten.
    /// </summary>
    public const int AnswerSynthesisMinChars = 320;

    /// <summary>Longest raw answer fed to the synthesizer, to bound prompt size and cost.</summary>
    public const int AnswerSynthesisMaxInputChars = 12000;

    /// <summary>
    /// Decide what the operator sees, given the raw best-task output and whatever the synthesizer
    /// returned. Separated from the model call so the fallback rules are unit-testable without a
    /// live provider — a mission must NEVER end up answerless because synthesis failed.
    /// </summary>
    /// <param name="synthesized">The synthesis call's typed result, or null if it was never made
    /// (no router, feature off, answer too short) or threw.</param>
    public static string SelectFinalAnswer(string rawAnswer, ModelCallResult? synthesized)
    {
        // v3.2.0: the provider's status decides. This used to test the response for an "ERROR:"
        // prefix, which meant a genuine answer that happened to discuss an error was one careless
        // rewording away from being discarded — and an empty generation was caught only because
        // Trim() made it zero-length, not because anything knew the call had failed.
        if (synthesized is null || !synthesized.Ok) return rawAnswer;
        var cleaned = synthesized.Content.Trim();
        return cleaned.Length == 0 ? rawAnswer : cleaned;
    }

    /// <summary>
    /// Whether this mission's raw answer is worth spending a synthesis call on.
    ///
    /// v3.1.0 (ADR-001): the feature gate arrives as a parameter rather than being read from a
    /// mutable static. This function decides what an operator sees; it should not be able to answer
    /// differently for two missions running at once because a setting moved between them.
    /// </summary>
    public static bool ShouldSynthesizeAnswer(string rawAnswer, bool synthesisEnabled) =>
        synthesisEnabled
        && !string.IsNullOrWhiteSpace(rawAnswer)
        && rawAnswer.Length >= AnswerSynthesisMinChars;

    /// <summary>
    /// The synthesis prompt. Deliberately constrains the model to REPHRASE what the colony
    /// produced — it must not add findings, and it must say so plainly when the mission did not
    /// succeed, rather than narrating a failure as though it were a result.
    /// </summary>
    public static string BuildAnswerSynthesisPrompt(Mission mission, string rawAnswer)
    {
        var input = TextUtil.Truncate(rawAnswer, AnswerSynthesisMaxInputChars, "\n...[truncated]");
        var outcome = mission.Status switch
        {
            MissionStatus.Complete => "The mission completed successfully.",
            MissionStatus.Partial => "The mission only PARTIALLY succeeded — say so, and say what is missing.",
            _ => "The mission FAILED — say so plainly and explain what went wrong. Do not present it as a success.",
        };
        return
            "You are writing the final answer a human operator reads after an automated mission.\n\n" +
            "Rules:\n" +
            "- Answer in plain English prose. No headings, no task lists, no status tables.\n" +
            "- Be concise: a short paragraph, or a few sentences.\n" +
            "- Report ONLY what the mission output below actually contains. Add nothing.\n" +
            "- If the output is code, a diff, or structured data, describe what it is and what it does\n" +
            "  rather than repeating it — the operator can expand the full detail separately.\n" +
            "- Do not mention tasks, ants, or the internals of how the work was done.\n\n" +
            $"{outcome}\n\n" +
            $"The operator originally asked:\n{mission.Goal}\n\n" +
            $"Mission output:\n{input}\n\n" +
            "Write the answer now:";
    }

    /// <summary>
    /// Produce the plain-English answer stored in <c>Mission.FinalResult</c>.
    ///
    /// Routed under the "scribe" role, which resolves through the normal route table (unknown roles
    /// fall back), so the model that writes answers can be pointed somewhere cheap in
    /// Settings → Model Routing without touching code.
    /// </summary>
    public string ComposeFinalAnswer(Mission mission, MissionContext context)
    {
        var raw = mission.UserResult ?? "";
        if (_router is null || !ShouldSynthesizeAnswer(raw, context.Options.AnswerSynthesis)) return raw;

        ModelCallResult? synthesized = null;
        try
        {
            synthesized = _router.GenerateTyped("scribe", BuildAnswerSynthesisPrompt(mission, raw),
                mission.Id, antName: "scribe");
        }
        catch (Exception ex)
        {
            // Synthesis is a presentation nicety. It must never be able to fail a finished mission.
            _memory.LogEvent(mission.Id, "answer_synthesis_failed",
                "Answer synthesis failed; showing the raw mission output instead.",
                metadata: new() { ["error"] = ex.Message });
        }
        return SelectFinalAnswer(raw, synthesized);
    }

    // ---- console rendering -----------------------------------------------------------------------

    public string ComposeCliResult(Mission mission)
    {
        var header = mission.Status == MissionStatus.Complete ? "Mission Complete"
            : mission.Status == MissionStatus.Partial ? "Mission Partial" : "Mission Failed";
        var score = mission.SuccessScore?.ToString() ?? "Not scored yet";
        var debugTrace = TextUtil.Truncate(mission.DebugResult ?? "", 5000, "...[debug trace truncated for CLI; full trace saved in debug_result]");
        var pending = _memory.CountPendingApprovals();
        var approvalNote = pending > 0
            ? $"\n\nPending Approval Requests: {pending}\nUse /approvals to list them."
            : "\n\nPending Approval Requests: 0";
        return $"{header}\n\nGoal:\n{mission.Goal}\n\nMission Status:\n{mission.Status.Value()}\n\nPheromone Score:\n{score}\n\n" +
               $"Best Output Task ID:\n{mission.BestOutputTaskId ?? "n/a"}\n\nUser Result:\n{mission.UserResult}{approvalNote}\n\nDebug Trace:\n\n{debugTrace}";
    }
}
