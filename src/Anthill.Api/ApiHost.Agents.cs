using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Modules.Reasoning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Anthill.Api;

/// <summary>
/// Installable command-line agents — what exists, what is present on this host, and installing one.
/// v3.8.39.
///
/// The composition root is allowed to name a module type; the CORE is not. This file lives in
/// Anthill.Api for that reason, the same way the reasoning module is constructed here rather than
/// in Anthill.Core.
/// </summary>
public static partial class ApiHost
{
    private static void MapAgentEndpoints(WebApplication app)
    {
        /*
         * What the colony can delegate to, and what is actually here.
         *
         * Reports the CATALOGUE and the HOST separately, because they answer different questions
         * and an operator needs both: "Anthill knows how to use Claude Code" and "Claude Code is on
         * this machine" have different remedies, and collapsing them into one boolean prints the
         * wrong instruction — the lesson `ollama_reachable` vs `ollama_model_present` taught in
         * v2.4.3.
         */
        app.MapGet("/agents", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            var refresh = ctx.Request.Query["refresh"] == "true";
            var statuses = AgentCliDiscovery.Scan(force: refresh);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                // Whether installing from the console is even possible here. The console must not
                // draw an Install button the server would refuse — the same rule the workspace
                // `deletable` flag follows.
                ["install_enabled"] = AnthillRuntime.EnableOperatorShell,
                // v0.3.8.41 — TOP LEVEL, because it is one directory for every agent rather than a
                // property of each. It was emitted per-row and read from the top by the console, so
                // the line telling an operator where their agents went never rendered at all.
                ["install_dir"] = AgentCliInstaller.AgentHome,
                ["install_disabled_reason"] = AnthillRuntime.EnableOperatorShell
                    ? null
                    : "Installing from the console runs a command on this host, so it needs the "
                    + "operator shell. Enable it in Configuration → Security, or run the install "
                    + "command yourself.",
                ["agents"] = statuses.Select(s => new Dictionary<string, object?>
                {
                    ["id"] = s.Agent.Id,
                    ["name"] = s.Agent.DisplayName,
                    ["vendor"] = s.Agent.Vendor,
                    ["binary"] = s.Agent.Binary,
                    ["installed"] = s.Installed,
                    ["version"] = s.Version,
                    ["unavailable_reason"] = s.Unavailable,
                    ["install_command"] = AgentCliCatalog.InstallHint(s.Agent),
                    // Printed, never run. A sign-in is an interactive act belonging to the person
                    // whose account it is, and Anthill holds no credential of theirs to use.
                    ["auth_command"] = s.Agent.AuthCommand,
                    ["docs_url"] = s.Agent.DocsUrl,
                    ["writes"] = s.Agent.Writes,
                }).ToList(),
                // v0.3.8.53 — the LOCAL runtime beside the vendor CLIs, because the no-account
                // path is the first thing a fresh install reaches for. Not a catalogue row: its
                // id would collide with the `ollama` provider the router already has.
                ["local"] = LocalRuntime(),
            });
        });

        /*
         * Install the local runtime (Ollama) — Windows end-to-end via winget; elsewhere an honest
         * refusal naming the exact command, because the vendor's Linux script needs root and
         * Anthill never sudos. Same operator-shell gate and same audit shape as an agent install:
         * this too runs a command on the host.
         */
        app.MapPost("/agents/local/install", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "operator_shell"); if (auth is not null) return auth;
            if (!AnthillRuntime.EnableOperatorShell)
                return ApiJson.Error(
                    "Installing from the console runs a command on this host and needs the operator "
                    + "shell. Enable it in Configuration → Security, or run the install command yourself.",
                    "shell_disabled");

            var who = ResolveIdentity(ctx)?.Username ?? "admin";
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "agent_install_started",
                $"Operator {who} started installing {LocalRuntimeInstaller.DisplayName}.", antName: "operator",
                metadata: new() { ["operator"] = who, ["agent"] = "local:ollama", ["command"] = LocalRuntimeInstaller.InstallHint });

            var result = LocalRuntimeInstaller.Install();

            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId,
                result.Ok ? "agent_install_succeeded" : "agent_install_failed",
                result.Ok
                    ? $"{LocalRuntimeInstaller.DisplayName} installed."
                    : $"{LocalRuntimeInstaller.DisplayName} failed to install: {result.Message}",
                antName: "operator",
                metadata: new() { ["operator"] = who, ["agent"] = "local:ollama", ["exit_code"] = result.ExitCode });

            return result.Ok
                ? ApiJson.Ok(new Dictionary<string, object?>
                  {
                      ["installed"] = LocalRuntimeInstaller.Probe().Installed,
                      ["output"] = result.Output,
                  }, result.Message)
                : ApiJson.Error(result.Message, "install_failed");
        });

        /*
         * Install one, from the catalogue only.
         *
         * Gated on `operator_shell` rather than a permission of its own, and that is deliberate:
         * this runs a package manager as this process's user and changes the machine globally. An
         * operator who has switched the shell off has said they do not want the console executing
         * commands here, and "but this one comes from our catalogue" is not a good enough reason to
         * override them. It is one toggle to turn back on.
         *
         * The command is looked up BY ID from the catalogue and never taken from the request, so
         * there is no request shape that can make this run something else.
         */
        app.MapPost("/agents/{id}/install", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "operator_shell"); if (auth is not null) return auth;
            if (!AnthillRuntime.EnableOperatorShell)
                return ApiJson.Error(
                    "Installing from the console runs a command on this host and needs the operator "
                    + "shell. Enable it in Configuration → Security, or run the install command yourself.",
                    "shell_disabled");

            var agent = AgentCliCatalog.ById(id);
            if (agent is null) return ApiJson.Error($"No such agent: {id}.", "not_found");

            var who = ResolveIdentity(ctx)?.Username ?? "admin";
            // Audited BEFORE it runs, matching /shell/exec: the record has to survive a command
            // that wedges the host, which is exactly the case anyone will want the record for.
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "agent_install_started",
                $"Operator {who} started installing {agent.DisplayName}.", antName: "operator",
                metadata: new() { ["operator"] = who, ["agent"] = agent.Id, ["command"] = AgentCliCatalog.InstallHint(agent) });

            var result = AgentCliInstaller.Install(agent);

            AgentCliDiscovery.Invalidate();   // so the next /agents read sees the new state

            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId,
                result.Ok ? "agent_install_succeeded" : "agent_install_failed",
                result.Ok
                    ? $"{agent.DisplayName} installed."
                    : $"{agent.DisplayName} failed to install: {result.Message}",
                antName: "operator",
                metadata: new() { ["operator"] = who, ["agent"] = agent.Id, ["exit_code"] = result.ExitCode });

            if (!result.Ok) return ApiJson.Error(result.Message, "install_failed");

            var status = AgentCliDiscovery.Scan(force: true)
                .FirstOrDefault(s => string.Equals(s.Agent.Id, agent.Id, StringComparison.Ordinal));

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["agent"] = agent.Id,
                ["installed"] = status?.Installed ?? false,
                ["version"] = status?.Version,
                // The next step, stated rather than implied. An installed agent that has never been
                // signed in to will fail its first mission with an auth error, and the operator
                // should hear about the login now rather than from a failed run.
                ["next_step"] = $"Sign in once in your own terminal: {agent.AuthCommand}",
                ["output"] = result.Output,
            }, $"{agent.DisplayName} installed.");
        });
    }

    /// <summary>The local runtime's row for /agents — same vocabulary as an agent row, so the
    /// console renders one card grammar for both.</summary>
    private static Dictionary<string, object?> LocalRuntime()
    {
        var (installed, version) = LocalRuntimeInstaller.Probe();
        return new Dictionary<string, object?>
        {
            ["id"] = "local:ollama",
            ["name"] = LocalRuntimeInstaller.DisplayName,
            ["vendor"] = "Ollama (open source)",
            ["installed"] = installed,
            ["version"] = version,
            ["unavailable_reason"] = installed ? null
                : "Not installed. Runs models on this machine — no account, no API key; "
                + "the colony's local provider routes to it.",
            ["install_command"] = LocalRuntimeInstaller.InstallHint,
            // Only Windows installs end-to-end from the console (winget). Elsewhere the deploy
            // shapes provision it, and a bare host needs the vendor's root-owned script.
            ["install_supported"] = OperatingSystem.IsWindows(),
            ["auth_command"] = null,
            ["docs_url"] = LocalRuntimeInstaller.DocsUrl,
            ["writes"] = false,
        };
    }
}
