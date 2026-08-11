using System.Diagnostics;
using Anthill.Modules.Homelab.Approvals;

namespace Anthill.Modules.Homelab.Actions;

/// <summary>
/// Docker container control, through the approval pipeline rather than around it. v0.3.8.40.
///
/// This runner adds no safety machinery of its own, and that is the point. By being an
/// <see cref="IHomelabActionRunner"/> it inherits every gate the framework already enforces: the
/// HOMELAB_STOP kill switch checked before anything runs, blast-radius scoring, the structural
/// approval gate (ActionLifecycle has no edge from RiskScored to Executing), a rollback note
/// required before a high-risk action may proceed, verification as the only door to success, and
/// an audit row per transition. A parallel "just run docker" path would have had none of that, and
/// would have been the single most dangerous thing in the repository.
///
/// THREE GATES, and each closes a different hole:
///
///  1. DEPLOYMENT MODE. Refuses outright unless the colony is a Server. On a laptop Anthill is a
///     personal assistant and the Docker socket is the operator's own; on a container host it is a
///     control plane and this is its job. Desktop is the default when detection cannot tell.
///  2. ALLOWLIST. Only the action types below, matched exactly. An action type absent from
///     ActionCatalog.Allowed never reaches a runner at all, so this is the second lock.
///  3. TARGET GUARD. The container name must match a conservative pattern. It becomes a process
///     argument, and while argv passing means it cannot be a shell injection, a name like `-v` or
///     `--privileged` would be read by docker as a FLAG. Validating the shape is what stops an
///     argument from becoming an option.
///
/// EXECUTE IS OFF BY DEFAULT. `docker_execute_enabled` gates it, and until an operator turns it on
/// this runner will describe precisely what it would do and refuse to do it. A dry run that is
/// honest is worth shipping on its own; an execute path nobody has watched is not.
/// </summary>
public sealed class DockerActionRunner : IHomelabActionRunner
{
    /// <summary>
    /// Lifecycle only. Deliberately no `docker run`, no `compose up`, no image pulls, no volume or
    /// network operations: those CREATE things, and creation has no rollback note that means
    /// anything. Restarting a container that was already running is recoverable by definition —
    /// starting one that was deliberately stopped is a judgment call, which is why it needs the
    /// same approval as the rest rather than a lower bar.
    /// </summary>
    private static readonly string[] Supported =
    {
        "start_container", "stop_container", "restart_container",
        // v0.3.8.40: compose, because down and up undo each other from the same file — the
        // reversibility container CREATION does not have. `docker run` is still absent for exactly
        // that reason, and `delete_container` remains structurally Forbidden in ActionCatalog.
        "compose_up", "compose_down",
    };

    /// <summary>Compose acts on a project (a directory of services), not a single container.</summary>
    private static bool IsCompose(string? actionType) =>
        (actionType ?? "").Trim().ToLowerInvariant() is "compose_up" or "compose_down";

    /// <summary>
    /// Docker's own name rule, minus the leading-character latitude. A name must start
    /// alphanumeric, which is exactly what stops `-rm` or `--privileged` being accepted as a
    /// target and then read by docker as an option.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex NamePattern =
        new("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<bool> _isServer;
    private readonly Func<string> _deploymentDescription;
    private readonly Func<bool> _executeEnabled;

    /// <summary>
    /// Gates are SUPPLIED, not read. This module references Anthill.SDK and nothing else — the
    /// homelab left the core in v3.8.7 and ModuleBoundaryTests enforces it by assembly reference —
    /// so reaching for AnthillRuntime here does not compile, and should not: a module that reads the
    /// core's configuration is the coupling that refactor removed.
    ///
    /// Read through delegates rather than captured as booleans because both are live settings. A
    /// value captured at construction would keep answering with whatever was true when the API
    /// booted, and an operator who turned execution off would find it still on.
    /// </summary>
    /// <param name="isServerDeployment">True when this colony is a server/container host.</param>
    /// <param name="deploymentDescription">How to describe the current mode in a refusal.</param>
    /// <param name="executeEnabled">True when approved container actions may actually run.</param>
    public DockerActionRunner(
        Func<bool> isServerDeployment,
        Func<string> deploymentDescription,
        Func<bool> executeEnabled)
    {
        _isServer = isServerDeployment;
        _deploymentDescription = deploymentDescription;
        _executeEnabled = executeEnabled;
    }

    public string Name => "docker";

    public bool CanRun(ActionProposal proposal) =>
        proposal is not null
        && Supported.Contains((proposal.ActionType ?? "").Trim().ToLowerInvariant())
        // Compose targets a project, a container action targets a container. Matching the target
        // kind keeps this runner from claiming a proposal whose shape it cannot actually act on.
        && string.Equals((proposal.TargetKind ?? "").Trim(),
                         IsCompose(proposal.ActionType) ? "compose_project" : "container",
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Says exactly what Execute would do, with the real command and the container's real state,
    /// and touches nothing. `docker inspect` is a read.
    ///
    /// Refusals are reported here rather than at execute time on purpose: the operator finds out
    /// this cannot run BEFORE they approve it, which is the entire value of a dry run.
    /// </summary>
    public Task<ActionRunResult> DryRunAsync(ActionProposal proposal, CancellationToken ct = default)
    {
        var refusal = Refuse(proposal, forExecution: false);
        if (refusal is not null) return Task.FromResult(new ActionRunResult(false, refusal));

        var verb = Verb(proposal.ActionType);
        var target = proposal.TargetId.Trim();

        string what;
        if (IsCompose(proposal.ActionType))
        {
            // `compose config --services` parses the file and lists what is in it. A read, and the
            // only honest way to say what "up" would actually start: the operator approved a
            // directory, not a list of services, and those are not the same thing until something
            // reads the file.
            var (cok, cout, cerr, cexit) = Run(
                new[] { "compose", "--project-directory", target, "config", "--services" }, ct);
            if (!cok) return Task.FromResult(new ActionRunResult(false, "docker is not installed or not on PATH."));
            if (cexit != 0)
                return Task.FromResult(new ActionRunResult(false,
                    $"No usable compose project at '{target}': {Tail(cerr, cout)}"));

            var services = cout.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            what = $"Would run: docker compose --project-directory {target} {verb}{(verb == "up" ? " -d" : "")}\n"
                 + $"{services.Count} service(s) in this project: {string.Join(", ", services)}.\n";
        }
        else
        {
            var (found, state, why) = Inspect(target, ct);
            if (!found)
                return Task.FromResult(new ActionRunResult(false,
                    $"No container named '{target}' on this host{(string.IsNullOrWhiteSpace(why) ? "." : $": {why}")}"));

            var noop = (verb, state) switch
            {
                ("start", "running") => " It is already running, so this would do nothing.",
                ("stop", "exited")   => " It is already stopped, so this would do nothing.",
                _ => "",
            };
            what = $"Would run: docker {verb} {target}\n"
                 + $"Container '{target}' is currently {state}.{noop}\n";
        }

        return Task.FromResult(new ActionRunResult(true,
            what
            + (_executeEnabled()
                ? "Execution is enabled; this will run once approved."
                : "Execution is DISABLED (docker_execute_enabled = false), so approving this will not run it.")));
    }

    public Task<ActionRunResult> ExecuteAsync(ActionProposal proposal, CancellationToken ct = default)
    {
        var refusal = Refuse(proposal, forExecution: true);
        if (refusal is not null) return Task.FromResult(new ActionRunResult(false, refusal));

        var verb = Verb(proposal.ActionType);
        var target = proposal.TargetId.Trim();

        var (ok, stdout, stderr, exit) = Run(ArgsFor(verb, target), ct);
        if (!ok) return Task.FromResult(new ActionRunResult(false, "docker is not installed or not on PATH."));

        return Task.FromResult(exit == 0
            ? new ActionRunResult(true, $"docker {verb} {target} succeeded.")
            : new ActionRunResult(false, $"docker {verb} {target} failed (exit {exit}): {Tail(stderr, stdout)}"));
    }

    /// <summary>
    /// Confirms the container actually reached the state the action was for.
    ///
    /// Safety rule 10 — never pretend something was fixed. A zero exit from `docker restart` means
    /// the daemon accepted the command, not that the container came back up: a container whose
    /// entrypoint dies immediately restarts "successfully" and is exited a second later. This asks
    /// the daemon what is true now.
    /// </summary>
    public Task<ActionRunResult> VerifyAsync(ActionProposal proposal, CancellationToken ct = default)
    {
        var target = (proposal.TargetId ?? "").Trim();

        if (IsCompose(proposal.ActionType))
        {
            // Safety rule 10 again: a zero exit from compose means the command was accepted. Whether
            // the stack is actually up is a different question, and this is the one that asks it.
            var (rok, rout, rerr, rexit) = Run(
                new[] { "compose", "--project-directory", target, "ps", "--status", "running", "-q" }, ct);
            if (!rok) return Task.FromResult(new ActionRunResult(false, "docker is not installed or not on PATH."));
            if (rexit != 0) return Task.FromResult(new ActionRunResult(false, $"Could not verify '{target}': {Tail(rerr, rout)}"));

            var running = rout.Split('\n').Count(x => x.Trim().Length > 0);
            var wantRunning = Verb(proposal.ActionType) == "up";
            return Task.FromResult(wantRunning
                ? (running > 0
                    ? new ActionRunResult(true, $"Verified: {running} service(s) running in '{target}'.")
                    : new ActionRunResult(false, $"Not verified: nothing is running in '{target}'."))
                : (running == 0
                    ? new ActionRunResult(true, $"Verified: nothing is running in '{target}'.")
                    : new ActionRunResult(false, $"Not verified: {running} service(s) still running in '{target}'.")));
        }

        var (found, state, why) = Inspect(target, ct);

        if (!found)
            return Task.FromResult(new ActionRunResult(false,
                $"Could not verify '{target}'{(string.IsNullOrWhiteSpace(why) ? "." : $": {why}")}"));

        var wanted = Verb(proposal.ActionType) == "stop" ? "exited" : "running";
        return Task.FromResult(state == wanted
            ? new ActionRunResult(true, $"Verified: '{target}' is {state}.")
            : new ActionRunResult(false, $"Not verified: '{target}' is {state}, expected {wanted}."));
    }

    /// <summary>
    /// Every reason this runner will not act, in one place, so dry run and execute can never
    /// disagree about what is permitted. Ordered cheapest first; the deployment gate needs no I/O.
    /// </summary>
    private string? Refuse(ActionProposal proposal, bool forExecution)
    {
        if (!_isServer())
            return "Container control is only available when Anthill runs as a server or container host. "
                 + $"This colony is running as {_deploymentDescription()}";

        var type = (proposal.ActionType ?? "").Trim().ToLowerInvariant();
        if (!Supported.Contains(type))
            return $"'{type}' is not a container action this runner performs.";

        var target = (proposal.TargetId ?? "").Trim();
        if (IsCompose(type))
        {
            // A compose target is a project DIRECTORY, so the container-name pattern would reject
            // every valid one. Different shape, different rule — but the same underlying concern:
            // a value starting with '-' is read by docker as an option rather than a path, and
            // `..` lets an approved target resolve somewhere the approver never saw.
            if (target.Length == 0 || target.StartsWith('-'))
                return $"'{target}' is not a valid compose project directory.";
            if (target.Contains("..", StringComparison.Ordinal))
                return "A compose project directory may not contain '..' — the approved path must be "
                     + "the path that runs.";
            if (!System.IO.Path.IsPathRooted(target))
                return $"'{target}' must be an absolute path, so the action means the same thing "
                     + "wherever Anthill is started from.";
        }
        else if (!NamePattern.IsMatch(target))
        {
            return $"'{target}' is not a valid container name. Names start with a letter or digit and "
                 + "contain only letters, digits, dots, dashes and underscores.";
        }

        if (forExecution && !_executeEnabled())
            return "Container execution is disabled. Set docker_execute_enabled to true in configuration "
                 + "to allow approved container actions to run. Dry run is available meanwhile.";

        return null;
    }

    private static string Verb(string? actionType) => (actionType ?? "").Trim().ToLowerInvariant() switch
    {
        "start_container" => "start",
        "stop_container" => "stop",
        "compose_up" => "up",
        "compose_down" => "down",
        _ => "restart",
    };

    /// <summary>
    /// The argv for one action. Compose runs against an explicit project directory rather than the
    /// process's working directory: `docker compose up` picks up whatever compose file happens to be
    /// where Anthill was started, which is a different stack from the one the operator approved.
    ///
    /// `-d` on up, because a compose that holds the terminal would hit the command timeout and be
    /// killed halfway through starting a stack.
    /// </summary>
    private static string[] ArgsFor(string verb, string target) => verb switch
    {
        "up"   => new[] { "compose", "--project-directory", target, "up", "-d" },
        "down" => new[] { "compose", "--project-directory", target, "down" },
        _      => new[] { verb, target },
    };

    /// <summary>Reads the container's state. A read — never used to change anything.</summary>
    private static (bool Found, string State, string Why) Inspect(string target, CancellationToken ct)
    {
        var (ok, stdout, stderr, exit) = Run(new[] { "inspect", "-f", "{{.State.Status}}", target }, ct);
        if (!ok) return (false, "", "docker is not installed or not on PATH");
        if (exit != 0) return (false, "", Tail(stderr, stdout));
        return (true, stdout.Trim(), "");
    }

    /// <summary>
    /// Starts docker directly — never through a shell, and with the target as its own argv entry.
    ///
    /// The name is validated above, so this is belt and braces rather than the only guard, and
    /// deliberately so: the two protect against different mistakes, and the argv form is what makes
    /// a future caller that forgets to validate merely wrong rather than dangerous.
    /// </summary>
    private static (bool Started, string Stdout, string Stderr, int ExitCode) Run(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        try { if (!p.Start()) return (false, "", "", -1); }
        catch (System.ComponentModel.Win32Exception) { return (false, "", "", -1); }
        catch (System.IO.FileNotFoundException) { return (false, "", "", -1); }

        // Both pipes drained concurrently: reading one to completion first deadlocks as soon as the
        // child fills the other, which docker does readily on a pull or a long error.
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);

        if (!p.WaitForExit((int)CommandTimeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (true, "", $"docker did not respond within {CommandTimeout.TotalSeconds:0}s", -1);
        }

        return (true, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), p.ExitCode);
    }

    private static string Tail(string stderr, string stdout)
    {
        var s = (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
        return s.Length <= 300 ? s : "…" + s[^300..];
    }
}
