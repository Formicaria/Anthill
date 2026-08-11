using Anthill.SDK.Reasoning;

namespace Anthill.Modules.Reasoning;

/// <summary>
/// Delegates a turn to an installed command-line agent. v3.8.39.
///
/// The colony gains Claude Code, Codex, Gemini CLI and the rest as interchangeable reasoning
/// providers: routed per role like any other, subject to the same contracts, the same budgets and
/// the same verification. From the Queen's side nothing is special about them.
///
/// Anthill starts a process and reads its output. It does not authenticate, does not hold a token
/// and does not know whose account is behind the tool — the operator ran the vendor's own login
/// once, and the tool carries its own session. There is therefore no credential in Anthill to
/// leak, expire, or have to refresh.
///
/// Failure is TYPED, never thrown and never a sentinel string, because that is the contract every
/// caller in this colony now depends on: a missing binary is NotAvailable, a timeout is Timeout, a
/// refused login is AuthError. An ant seeing NotAvailable can route elsewhere; an ant seeing a
/// string beginning "ERROR:" could only guess.
/// </summary>
public sealed class AgentCliProvider : IReasoningProvider
{
    private readonly AgentCli _agent;
    private readonly TimeSpan _timeout;
    private readonly string? _workingDirectory;

    /// <param name="workingDirectory">
    /// The workspace this agent is confined to. This is what keeps a writing agent inside the same
    /// boundary as every other actor: the colony's rule is that the active checkout is never an
    /// agent scratchpad, and an agent from another vendor does not get an exemption from it.
    ///
    /// Null means UNCONFINED, and for an agent that writes that is a refusal, not a default — see
    /// <see cref="Confinement"/>. It stays nullable because a read-only agent has nothing to be
    /// confined from, and because the alternative, a required parameter, would be satisfied by
    /// whatever string a caller had to hand. This parameter previously had exactly one production
    /// caller and that caller did not pass it; the check is what makes that unrepeatable.
    /// </param>
    public AgentCliProvider(AgentCli agent, TimeSpan? timeout = null, string? workingDirectory = null)
    {
        _agent = agent;
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        _workingDirectory = workingDirectory;
    }

    public ModelResponse Send(ModelRequest request, int retries = 2)
    {
        var prompt = Flatten(request);
        if (string.IsNullOrWhiteSpace(prompt))
            return Fail(ModelCallOutcome.ConfigError, "No prompt to send.");

        // Confinement is checked BEFORE the process starts, because after it starts there is nothing
        // left to check: an agent that edits files has already edited them by the time it exits.
        var confinement = Confinement();
        if (confinement is not null) return confinement;

        // Retries are deliberately NOT applied here. A CLI agent turn is minutes long, may have
        // already edited files, and is not idempotent — re-running one after a timeout could apply
        // the same change twice. The bounded-retry policy that suits a stateless HTTP call is the
        // wrong policy for a process that acts, so the parameter is accepted and ignored rather
        // than silently doing the dangerous thing.
        _ = retries;

        var args = AgentCliCatalog.BuildArgs(_agent, prompt);
        var (started, stdout, stderr, exit) =
            AgentCliDiscovery.Run(_agent.Binary, args, _timeout, _workingDirectory);

        if (!started)
            return Fail(ModelCallOutcome.NotAvailable,
                $"{_agent.DisplayName} is not installed. Install it from the Agents page, or with: "
                + AgentCliCatalog.InstallHint(_agent));

        if (exit != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return Fail(Classify(detail, stderr), Describe(detail));
        }

        if (string.IsNullOrWhiteSpace(stdout))
            return Fail(ModelCallOutcome.Empty, $"{_agent.DisplayName} exited cleanly but said nothing.");

        return new ModelResponse
        {
            Status = ModelCallOutcome.Ok,
            Content = stdout.Trim(),
            Provider = _agent.Id,
            Model = _agent.DisplayName,
            FinishReason = "exit_0",
        };
    }

    /// <summary>
    /// Whether this agent may run at all, given where it would run. Null means yes. v0.3.8.41.
    ///
    /// Only agents that ACT are gated. One that merely answers is a text pipe and its working
    /// directory is uninteresting, so an operator who has routed a read-only agent is not stopped by
    /// a rule that protects against writes — <see cref="AgentCli.Writes"/> finally decides something
    /// here, having previously been a flag that was serialised to the console and consulted nowhere.
    ///
    /// A REFUSAL, not a fallback to some safe-ish directory. Two candidates were available and both
    /// are wrong: the current directory is the bug, and silently inventing a temp directory would
    /// mean the agent's work lands somewhere the operator never looks and nothing collects — a
    /// mission that appears to succeed and changes nothing is harder to diagnose than one that
    /// refuses and says why.
    ///
    /// The existence check is not paranoia about a missing directory. Process.Start with a working
    /// directory that is not there throws the same Win32Exception as a binary that is not there, and
    /// this provider maps that to "the agent is not installed" — an error naming the wrong problem
    /// and prescribing an install that would not fix it.
    /// </summary>
    private ModelResponse? Confinement()
    {
        if (!_agent.Writes) return null;

        if (string.IsNullOrWhiteSpace(_workingDirectory))
            return Fail(ModelCallOutcome.ConfigError,
                $"{_agent.DisplayName} edits files and runs commands, so it will not be started without a "
                + "workspace to be confined to — unconfined it would act in whatever directory Anthill "
                + "itself was started from. Set an agent workspace in Configuration → Workspace.");

        if (!Directory.Exists(_workingDirectory))
            return Fail(ModelCallOutcome.ConfigError,
                $"{_agent.DisplayName} is confined to '{_workingDirectory}', which does not exist. "
                + "Create it, or set an agent workspace in Configuration → Workspace.");

        return null;
    }

    /// <summary>
    /// Everything the request carries, as one prompt.
    ///
    /// A CLI agent takes text, not a role-tagged message array, so the roles are labelled in the
    /// text rather than dropped — a system instruction that silently vanished on the way to the
    /// agent would produce a confident answer that ignored its constraints, which is the failure
    /// this colony is least able to detect afterwards.
    /// </summary>
    private static string Flatten(ModelRequest request)
    {
        // Messages is `required` and non-nullable, and Content is a positional string — a null-guard
        // on either is dead code the compiler would warn about, which is a poor trade for a check
        // the type system already makes.
        var parts = new List<string>();
        foreach (var m in request.Messages)
        {
            var text = m.Content.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            parts.Add(string.Equals(m.Role, ModelMessage.User, StringComparison.OrdinalIgnoreCase)
                ? text
                : $"[{m.Role}]\n{text}");
        }
        return string.Join("\n\n", parts).Trim();
    }

    /// <summary>
    /// Map the tool's own failure onto the colony's vocabulary.
    ///
    /// Text matching, and worth being honest about: these are other vendors' messages and they will
    /// change. It is a hint that improves the operator's next step, never a control-flow decision —
    /// every branch here ends in a failed call either way. What matters is that the STATUS is typed,
    /// so a caller routes on the enum rather than on someone else's prose.
    /// </summary>
    private static ModelCallOutcome Classify(string detail, string stderr)
    {
        var t = (detail + " " + stderr).ToLowerInvariant();
        if (t.Contains("timed out")) return ModelCallOutcome.Timeout;
        if (t.Contains("not logged in") || t.Contains("unauthorized") || t.Contains("authentication")
            || t.Contains("login") || t.Contains("api key")) return ModelCallOutcome.AuthError;
        if (t.Contains("rate limit") || t.Contains("quota") || t.Contains("429")) return ModelCallOutcome.HttpError;
        return ModelCallOutcome.Error;
    }

    private string Describe(string detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? $"{_agent.DisplayName} failed without saying why."
            : $"{_agent.DisplayName}: {detail.Trim()}";

    private ModelResponse Fail(ModelCallOutcome status, string message) => new()
    {
        Status = status,
        Content = message,
        Provider = _agent.Id,
        Model = _agent.DisplayName,
    };
}

/// <summary>
/// Builds an <see cref="AgentCliProvider"/> for any catalogued agent id. v3.8.39.
///
/// <see cref="CanServe"/> answers from the catalogue alone — no PATH lookup, no process, no probe —
/// because the interface forbids I/O here and means it: this is asked on the mission hot path. An
/// agent that turns out not to be installed is discovered when it is CALLED, and comes back as a
/// typed NotAvailable naming the install command.
/// </summary>
public sealed class AgentCliProviderFactory : IReasoningProviderFactory
{
    public bool CanServe(string providerId) => AgentCliCatalog.ById(providerId) is not null;

    public IReasoningProvider Create(ReasoningProviderContext context)
    {
        var agent = AgentCliCatalog.ById(context.ProviderId)
            ?? throw new InvalidOperationException(
                $"CanServe said yes to '{context.ProviderId}' and Create found no such agent — the two disagree.");

        // v3.8.39 — honour the operator's configured per-call deadline instead of a private default.
        //
        // This shipped with a hardcoded ten minutes, and the first live test found why that is
        // wrong: `opencode run` did not return, and the request sat for the full ten minutes with
        // nothing an operator could do but wait. `ModelCallTimeoutSeconds` is read on every request
        // precisely so a colony can bound a slow provider, and a provider that ignores it is a
        // setting that silently does nothing — the exact failure IReasoningRuntimeOptions was
        // introduced to prevent.
        //
        // Agents are slower than an HTTP call, so the configured value is the FLOOR of a longer
        // allowance rather than the value itself: a real coding turn legitimately runs for minutes,
        // and an operator's 120s HTTP deadline would abort work that was going fine.
        var seconds = Math.Max(context.Options.ModelCallTimeoutSeconds, 1);

        // v0.3.8.41 — THE WORKING DIRECTORY IS PASSED. It was not, and that was the whole defect.
        //
        // AgentCliProvider has taken a workingDirectory since it was written, documented as "what
        // keeps a writing agent inside the same boundary as every other actor". This line — the only
        // place production ever constructs one — omitted it. The parameter defaulted to null, null
        // meant "don't set ProcessStartInfo.WorkingDirectory", and a child process that is not given
        // one inherits its parent's: the directory the API host was started from, i.e. the
        // operator's live checkout.
        //
        // So routing an ant to Claude Code handed a tool with Writes = true a shell in the source
        // tree. Every guard the colony has for its own coder — SandboxWorkspace, WorkspacePathGuard,
        // PatchSet review, the approve-then-apply gate — sits on a path that this went around
        // entirely, and it did so silently, because the agent's edits are simply not events Anthill
        // ever saw.
        //
        // This is the failure mode this repository keeps naming: not an absent feature, a feature
        // PRESENT AND WIRED WRONG. A sweep for "is confinement implemented?" finds a documented
        // parameter and a Writes flag and answers yes.
        return new AgentCliProvider(agent, TimeSpan.FromSeconds(seconds * 4),
            context.Options.AgentWorkspaceRoot);
    }
}
