using System.Text.Json.Serialization;
using Anthill.SDK.Contracts;

namespace Anthill.SDK.Tools;

// v3.8.10 — moved out of Anthill.Core.Domain, which v3.8.9 made possible: this type's only
// dependencies are FailureClass and FailureClassify, and both joined the SDK in that release.
//
// NOTE the name. Anthill.Core.Contracts declares a DIFFERENT ToolResult — the contract-shaped one —
// and five call sites disambiguate against it with `Contracts.ToolResult`. Those are untouched and
// must stay that way. This is the dispatch result: what every ITool returns.


public sealed class ToolResult
{
    public string ToolName { get; set; } = "";
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public string? Error { get; set; }

    /// <summary>
    /// v3.4.0 — WHY it failed, typed. <c>None</c> when it succeeded.
    ///
    /// The registry already knew the difference between "no such tool", "you may not run that" and
    /// "it threw", and flattened all three into a sentence — with <c>authorization_denied:</c> as a
    /// prose marker for one of them. That is the same recover-the-status-by-reading-the-text pattern
    /// v3.2.0 removed from the ant contract, one layer down.
    ///
    /// It earns its place because the agent loop has to DECIDE what to do next, and the right next
    /// move differs by class: a denial means take another route, a timeout means try again, a
    /// validation failure means fix the arguments and call the same tool. A bool cannot say which,
    /// so the loop either treats every failure identically or starts matching on error strings.
    /// </summary>
    public FailureClass Failure { get; set; } = FailureClass.None;

    /// <summary>
    /// Whether the identical call could plausibly succeed if repeated. DERIVED from
    /// <see cref="Failure"/> rather than stored, so the two can never contradict each other, and
    /// there is one definition of retryable in the codebase rather than two that drift.
    /// </summary>
    public bool Retryable => FailureClassify.IsRetryable(Failure);

    public ToolResult() { }
    public ToolResult(string toolName, bool success, string output, string? error = null,
        FailureClass failure = FailureClass.None)
    {
        ToolName = toolName; Success = success; Output = output; Error = error;
        // A failure that names no class is an InternalDefect, not an unclassified mystery: the tool
        // failed and did not say why, which is a defect in the tool. Leaving it None would make
        // "succeeded" and "failed for unstated reasons" the same value.
        Failure = failure != FailureClass.None ? failure
            : success ? FailureClass.None : FailureClass.InternalDefect;
    }
}
