namespace Anthill.SDK.Reasoning;

/// <summary>
/// v0.3.8.51 — what the current flow is ALLOWED, carried to the reasoning provider that spawns a
/// working agent (field report: the colony's own Claude Code worker sat behind "requires approval"
/// prompts that a headless run can never answer, so every Edit/Write/Bash died and self-improvement
/// missions could read but never act).
///
/// The operator already answers this question once, in chat: Manual approval / Automatically
/// approve / Skip all approvals, plus any directory gates they opened for the project. This scope
/// is how that ANSWER reaches the agent CLI invocation instead of stopping at Anthill's own gate
/// while the delegated agent runs locked down.
///
/// AsyncLocal like <see cref="ModelCallScope"/>, entered by orchestration around ant execution.
/// Absent scope means NOTHING IS GRANTED beyond the agent's own defaults — absence is not consent.
/// </summary>
public static class AgentAccessScope
{
    /// <summary>
    /// <paramref name="PolicyWire"/> is the conversation's EFFECTIVE policy in wire form:
    /// "ask" | "autoapprove" | "bypass". <paramref name="GrantedDirectories"/> are the absolute
    /// paths the operator explicitly opened for this project — each one becomes additional reach
    /// for the agent, and nothing else does.
    ///
    /// <paramref name="ConfinedWorkspace"/> says WHICH TREE the agent stands in, because the same
    /// policy means different things in different trees. A mission runs in a DISPOSABLE sandbox:
    /// under Manual approval the mission itself was the operator's yes, so edits there are the
    /// approved work. The chat lane stands in a REAL directory: under Manual approval its edits
    /// would be un-asked side effects on live files, so it gets nothing and proposes a mission
    /// instead — which is where the sandbox and the patch pipeline live.
    /// </summary>
    /// <summary>
    /// <paramref name="WorkingDirectory"/> — v0.3.8.52 (field report: every project's chat ran in
    /// the same tree): the directory this flow's agent should STAND IN, when the caller knows one
    /// (the conversation's own project directory, resolved through ProjectRoots). Null keeps the
    /// provider's static default — the shared agent workspace root — which is the old behaviour
    /// and still right for callers with no project in hand.
    /// </summary>
    /// <summary>
    /// <paramref name="RoleMayWrite"/> — v0.3.8.93: whether the ROLE this flow is executing is one
    /// whose contract permits producing changes at all (<c>ProposePatches</c> or
    /// <c>WriteWorkspace</c> in the ant registry).
    ///
    /// The operator's policy and the role's contract answer DIFFERENT questions and both must say
    /// yes. Until this release the scope carried only the policy, so "Skip all approvals" handed
    /// <c>--dangerously-skip-permissions</c> to whatever role happened to be routed to an agent
    /// CLI — a read-only researcher included. The operator's bypass skips the operator's own
    /// prompts; it was never a grant of write capability to a role whose contract has none, and
    /// the translation layer (<c>AgentCliCatalog.BuildAccessArgs</c> /
    /// <c>BuildLocalSettingsJson</c>) now clamps on this flag before consulting the policy.
    ///
    /// Defaults TRUE, deliberately: the flows that do not set it are the operator's own direct
    /// agent lane, where the actor is the operator's agent under the operator's policy and there
    /// is no role contract to project. Mission dispatch, which DOES act under a contract, sets it
    /// from the registry on every task. The default preserves those callers exactly; the flag only
    /// ever narrows.
    /// </summary>
    public sealed record Context(
        string PolicyWire,
        IReadOnlyList<string> GrantedDirectories,
        bool ConfinedWorkspace = false,
        string? WorkingDirectory = null,
        bool RoleMayWrite = true);

    private static readonly AsyncLocal<Context?> Ambient = new();

    public static Context? Current => Ambient.Value;

    public static IDisposable Enter(string policyWire, IReadOnlyList<string>? grantedDirectories = null,
        bool confinedWorkspace = false, string? workingDirectory = null, bool roleMayWrite = true)
    {
        var previous = Ambient.Value;
        Ambient.Value = new Context(
            string.IsNullOrWhiteSpace(policyWire) ? "ask" : policyWire.ToLowerInvariant(),
            grantedDirectories ?? Array.Empty<string>(),
            confinedWorkspace,
            string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            roleMayWrite);
        return new Scope(previous);
    }

    private sealed class Scope(Context? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
