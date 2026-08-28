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
public sealed class AgentCliProvider : IReasoningProvider, IStreamingReasoningProvider
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
        var (prompt, system) = Split(request);
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

        // v0.3.8.51: the operator's approval gate rides along as this agent's own flags — the fix
        // for a headless worker whose every mutating call died at prompts nobody could answer.
        // Second field round: AND as the agent's project-level settings file, because the colony's
        // own probe showed flags alone fall through to harness defaults without one.
        var access = Anthill.SDK.Reasoning.AgentAccessScope.Current;
        // v0.3.8.52: the flow's own directory (the conversation's project tree) wins over the
        // static default — see EffectiveWorkingDirectory. One resolution, both transports.
        var cwd = EffectiveWorkingDirectory(access);
        AgentCliCatalog.MaterializeLocalSettings(_agent, cwd, access);
        var args = AgentCliCatalog.BuildArgs(_agent, prompt, system)
            .Concat(AgentCliCatalog.BuildAccessArgs(_agent, access))
            .ToList();
        // v0.3.8.67: the prompt goes on stdin for agents that read it there, and is NOT also an
        // argument — BuildArgs has no {prompt} to substitute for those agents, so there is exactly
        // one copy and exactly one channel.
        var (started, stdout, stderr, exit) =
            AgentCliDiscovery.Run(_agent.Binary, args, _timeout, cwd,
                stdin: _agent.PromptOnStdin ? prompt : null);

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
    /// v0.3.8.47 — the same run, streamed: stdout lines reach the operator as the agent writes
    /// them. Everything else is Send's behaviour verbatim — same confinement check, same
    /// no-retry rule (an agent turn is not idempotent), same classification of a bad exit. The
    /// ambient ModelCallScope token kills the process on cancel, so the ■ reaches the agent.
    /// </summary>
    public ModelResponse SendStreaming(ModelRequest request, Action<string> onDelta, int retries = 2)
    {
        var (prompt, system) = Split(request);
        if (string.IsNullOrWhiteSpace(prompt))
            return Fail(ModelCallOutcome.ConfigError, "No prompt to send.");
        var confinement = Confinement();
        if (confinement is not null) return confinement;
        _ = retries;   // deliberately ignored — see Send.

        // v0.3.8.47: agents WITH a streaming mode (Claude Code's stream-json) get real deltas —
        // each stdout line is an NDJSON event, and only its TEXT reaches the operator. Agents
        // without one keep honest line streaming. The final content comes from the result event
        // when there is one, because raw NDJSON is transport, not answer.
        var hasStreamMode = _agent.StreamArgs is not null;
        // v0.3.8.51: same access translation as Send — one policy, both transports, both channels.
        var streamAccess = Anthill.SDK.Reasoning.AgentAccessScope.Current;
        var streamCwd = EffectiveWorkingDirectory(streamAccess);
        AgentCliCatalog.MaterializeLocalSettings(_agent, streamCwd, streamAccess);
        var args = AgentCliCatalog.BuildStreamArgs(_agent, prompt, system)
            .Concat(AgentCliCatalog.BuildAccessArgs(_agent, streamAccess))
            .ToList();
        var streamedText = new System.Text.StringBuilder();
        string? resultText = null;
        var sawTokenDelta = false;
        Action<string> sink = !hasStreamMode ? onDelta : line =>
        {
            var isTokenDelta = line.Contains("\"stream_event\"", StringComparison.Ordinal);
            var (text, result) = ParseStreamEvent(line);
            if (text is not null)
            {
                // Once token deltas flow, the whole-message assistant event that follows them is
                // a repeat of text the operator already watched arrive — emitting it would double
                // the answer on screen.
                if (isTokenDelta) sawTokenDelta = true;
                if (isTokenDelta || !sawTokenDelta) { streamedText.Append(text); onDelta(text); }
            }
            if (result is not null) resultText = result;
        };
        var (started, stdout, stderr, exit) = AgentCliDiscovery.RunStreaming(
            _agent.Binary, args, _timeout, sink, ModelCallScope.Current, streamCwd,
            stdin: _agent.PromptOnStdin ? prompt : null);
        if (hasStreamMode && exit == 0)
            stdout = !string.IsNullOrWhiteSpace(resultText) ? resultText
                   : streamedText.Length > 0 ? streamedText.ToString() : stdout;

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
    /// One NDJSON stream event → (delta text, final result). Unparseable or uninteresting lines
    /// are (null, null): verbose noise stays out of the transcript. Never throws.
    /// </summary>
    internal static (string? Text, string? Result) ParseStreamEvent(string line)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "result" && root.TryGetProperty("result", out var r))
                return (null, r.GetString());
            // Token-level deltas (--include-partial-messages): the event wraps the API's own
            // content_block_delta. When these arrive, whole-message assistant events are SKIPPED
            // by the caller's dedupe below being unnecessary — deltas and the result are enough.
            if (type == "stream_event" && root.TryGetProperty("event", out var ev)
                && ev.TryGetProperty("type", out var et) && et.GetString() == "content_block_delta"
                && ev.TryGetProperty("delta", out var d)
                && d.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta"
                && d.TryGetProperty("text", out var dx))
                return (dx.GetString(), null);
            if (type == "assistant" && root.TryGetProperty("message", out var m)
                && m.TryGetProperty("content", out var c)
                && c.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var block in c.EnumerateArray())
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                        && block.TryGetProperty("text", out var tx))
                        sb.Append(tx.GetString());
                return (sb.Length > 0 ? sb.ToString() : null, null);
            }
            return (null, null);
        }
        catch { return (null, null); }
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
    /// <summary>
    /// v0.3.8.52: the one working-directory resolution, both transports. The ambient flow's own
    /// directory — the conversation's project tree, placed on <see cref="Anthill.SDK.Reasoning.AgentAccessScope"/>
    /// by the runner that KNOWS the project — wins over the static shared workspace root this
    /// provider was constructed with. Null stays null, and Confinement refuses a writing agent
    /// exactly as before: a missing boundary is a refusal, never a fallback into Anthill's own cwd.
    /// </summary>
    private string? EffectiveWorkingDirectory(Anthill.SDK.Reasoning.AgentAccessScope.Context? access) =>
        string.IsNullOrWhiteSpace(access?.WorkingDirectory) ? _workingDirectory : access!.WorkingDirectory;

    private ModelResponse? Confinement()
    {
        if (!_agent.Writes) return null;

        var access = Anthill.SDK.Reasoning.AgentAccessScope.Current;

        // v0.3.8.97 — THE WORKTREE GATE. A mission flow whose role may write and whose isolated
        // worktree is missing or rejected says so explicitly, and a writing agent is refused
        // BEFORE the process starts. This cannot be inferred from a null working directory —
        // EffectiveWorkingDirectory below falls back to the static agent workspace root, and the
        // mission lane resolves the project's LIVE checkout as its directory — so without the flag
        // this provider would happily stand a writing agent in the operator's real tree the moment
        // worktree preparation failed. Prompt-only restraint is not confinement.
        if (access is { MissionWorktreeMissing: true })
            return Fail(ModelCallOutcome.ConfigError,
                $"{_agent.DisplayName} writes files and this mission's role may write, but the "
                + "mission has no usable isolated worktree — refused by the worktree gate "
                + "(AgentCliProvider.Confinement) before the process started. A writable agent CLI "
                + "is never run in the live project; fix or retry the mission workspace instead.");

        var cwd = EffectiveWorkingDirectory(access);
        if (string.IsNullOrWhiteSpace(cwd))
            return Fail(ModelCallOutcome.ConfigError,
                $"{_agent.DisplayName} edits files and runs commands, so it will not be started without a "
                + "workspace to be confined to — unconfined it would act in whatever directory Anthill "
                + "itself was started from. Set an agent workspace in Configuration → Workspace.");

        if (!Directory.Exists(cwd))
            return Fail(ModelCallOutcome.ConfigError,
                $"{_agent.DisplayName} is confined to '{cwd}', which does not exist. "
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
    /// <summary>
    /// v0.3.8.59 (PLAN.md §1b S9) — the CONTRACT and the TASK travel separately.
    ///
    /// This was one method, `Flatten`, that joined every message into a single string and prefixed
    /// non-user roles with a literal <c>[system]</c> header. That string then became the argument to
    /// <c>-p</c>, which is a USER TURN. So the colony's role contract reached the agent as a user
    /// message asserting a persona, mission ids, tool permissions and a required output format, with
    /// a line of prose claiming to be the system. An agent CLI refused whole missions on that basis
    /// and was right to: obeying it is obeying anything wearing the same costume.
    ///
    /// System messages now go to the agent's own system flag when it has one. When it does NOT they
    /// are folded into the prompt as before — because a contract that reaches the worker in a
    /// suspicious shape is still better than a worker with no contract — but WITHOUT the
    /// <c>[system]</c> literal, which contributed nothing except the impersonation.
    /// </summary>
    private (string Task, string? System) Split(ModelRequest request)
    {
        var system = new List<string>();
        var task = new List<string>();

        foreach (var m in request.Messages)
        {
            var text = m.Content.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (string.Equals(m.Role, ModelMessage.System, StringComparison.OrdinalIgnoreCase))
                system.Add(text);
            else if (string.Equals(m.Role, ModelMessage.User, StringComparison.OrdinalIgnoreCase))
                task.Add(text);
            else
                // assistant and tool turns keep a label: it is transcript framing, describing who
                // said a thing, not a claim of authority over the reader.
                task.Add($"[{m.Role}]\n{text}");
        }

        var contract = system.Count == 0 ? null : string.Join("\n\n", system);

        if (contract is not null && _agent.SystemPromptArgs is null)
        {
            // No system channel on this agent. The contract leads the prompt, plainly, and the
            // caller gets null back so nothing tries to pass a flag that does not exist.
            task.Insert(0, contract);
            contract = null;
        }

        return (string.Join("\n\n", task).Trim(), contract);
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
