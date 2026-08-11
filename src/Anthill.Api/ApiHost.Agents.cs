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
                    ["install_command"] = s.Agent.InstallCommand,
                    // Printed, never run. A sign-in is an interactive act belonging to the person
                    // whose account it is, and Anthill holds no credential of theirs to use.
                    ["auth_command"] = s.Agent.AuthCommand,
                    ["docs_url"] = s.Agent.DocsUrl,
                    ["writes"] = s.Agent.Writes,
                }).ToList(),
            });
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
                metadata: new() { ["operator"] = who, ["agent"] = agent.Id, ["command"] = agent.InstallCommand });

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
}
