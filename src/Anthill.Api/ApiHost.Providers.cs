using System.Reflection;
using Anthill.Core.Agents;
using Anthill.Core.Shadow;
using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Conversations;   // v3.7.0: conversations, escalation policy and run state
using Anthill.Core.Diagnostics;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Orchestration;
using Anthill.Core.Planning;
using Anthill.Core.Readiness;
using Anthill.Core.Sandbox;   // LoopBudget — the agent loop's bounds
using Anthill.Core.Security;
using Anthill.Core.Tools;      // ToolInventory, ToolAuthorization — the /tools report
// `Task` here is Anthill.Core.Domain.Task (the mission task). The threading one must be named.
using ThreadingTask = System.Threading.Tasks.Task;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;


namespace Anthill.Api;

/// <summary>
/// Reasoning providers, routing and model discovery.
///
/// v3.8.17 — split out of ApiHost.cs, which was 3,294 lines and 102 endpoints. Same class,
/// same behaviour: ApiHost has been `public static partial` with eight files since the homelab
/// moved, so this is where the file was always going to divide.
/// </summary>
public static partial class ApiHost
{
    // ---- Model provider connections (API keys for OpenAI/Anthropic/Perplexity/OpenRouter/...) ----
    private static void MapProviderEndpoints(WebApplication app)
    {
        // Static catalog metadata: which providers exist, whether they need a key, curated model
        // lists, and where to go get a key. No secrets here — safe to read with read_providers.
        app.MapGet("/providers/catalog", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_providers"); if (auth is not null) return auth;
            var catalog = ProviderCatalog.All.Select(p => new Dictionary<string, object?>
            {
                ["provider"] = p.Id, ["name"] = p.Name, ["kind"] = p.Kind, ["description"] = p.Description,
                ["requires_key"] = p.RequiresKey, ["default_endpoint"] = p.DefaultEndpoint,
                ["key_help_url"] = p.KeyHelpUrl, ["default_model"] = p.DefaultModel, ["models"] = p.Models,
                ["agent"] = false, ["installed"] = true,
            }).ToList();

            /*
             * v3.8.39 — installed CLI agents join the routing choices, so an ant can be given one.
             *
             * Composed HERE rather than in ProviderCatalog because that list lives in Anthill.SDK,
             * which is contracts-only and may not reference a module. The API is the composition
             * root, already constructs the reasoning module, and joining two catalogues is exactly
             * the work a composition root exists to do.
             *
             * `default_model` is the agent's own name and must never be empty. ModelRouter treats a
             * non-keyed provider with no model as a local model needing resolution, and would ask
             * Ollama to resolve a model for Claude Code — an answer that cannot exist. Carrying a
             * model keeps that branch unreached.
             *
             * Uninstalled agents are listed too, marked installed:false. Hiding them would leave an
             * operator wondering why Anthill offers Codex on one screen and not another; showing
             * them with their state is the rule the agents page already follows.
             */
            catalog.AddRange(AgentCliDiscovery.Scan().Select(s => new Dictionary<string, object?>
            {
                ["provider"] = s.Agent.Id,
                ["name"] = s.Agent.DisplayName + " (agent)",
                ["kind"] = "agent",
                ["description"] = s.Installed
                    ? $"Delegates the turn to {s.Agent.DisplayName} on this machine, signed in as you. "
                    + "Anthill starts it and never holds your credentials."
                    : $"Not installed. {s.Agent.InstallCommand}",
                ["requires_key"] = false,
                ["default_endpoint"] = null,
                ["key_help_url"] = s.Agent.DocsUrl,
                ["default_model"] = s.Agent.DisplayName,
                ["models"] = new[] { s.Agent.DisplayName },
                ["agent"] = true,
                ["installed"] = s.Installed,
            }));

            return ApiJson.Ok(catalog);
        });

        // Secret-free connection status for every keyed provider (configured or not).
        app.MapGet("/providers", (HttpContext ctx) =>
            RequireAuth(ctx, "read_providers") ?? ApiJson.Ok(Queen.Memory.ListProviderConnections()));

        /*
         * v3.3.0 (ADR-006): what each provider/model pair can actually DO.
         *
         * Capability is a property of the MODEL, not of the provider that serves it — a tool-capable
         * model on Ollama is tool-capable, and a text-only model on OpenAI is not made tool-capable
         * by the company hosting it. So this reports per model, and the operator can see why a role
         * pinned to one model gets tools and another does not.
         *
         * Unknown resolves to text-only rather than to a blank: an operator reading "no capabilities
         * listed" would reasonably assume the page was broken, whereas "text only" is the actual,
         * deliberate, fail-closed answer.
         */
        app.MapGet("/providers/capabilities", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_providers"); if (auth is not null) return auth;

            // v3.3.0: DISCOVERED capabilities where the runtime publishes them. Ollama reports a
            // per-model `capabilities` array on /api/tags, and it is authoritative in a way a name
            // table can never be: against three real local models the hand-written table was wrong
            // twice — it called gemma4:31b text-only when Ollama reports tools AND thinking, so the
            // operator's most capable local model would never have been offered a tool.
            //
            // Best-effort by design. An unreachable Ollama must not fail the whole page; the report
            // falls back to declared capabilities and says which it used, per provider.
            var discovered = await DiscoverOllamaModelsAsync();

            // Seed the cache the MODEL CALL PATH reads. Before this, discovery informed the report
            // and nothing else: the page said gemma4:31b supports tools while OllamaClient stripped
            // them from every request, and the model — never shown a tool — answered from priors.
            // A page that reports capabilities the runtime does not act on is a lie with a UI.
            OllamaCapabilityCache.Warm(AnthillRuntime.OllamaHost);

            var report = new List<Dictionary<string, object?>>();
            foreach (var p in ProviderCatalog.All)
            {
                var isOllama = string.Equals(p.Id, "ollama", StringComparison.OrdinalIgnoreCase);
                var useDiscovered = isOllama && discovered.Count > 0;
                // A provider whose catalog list is empty does not have "no models" — it has a
                // DYNAMIC list. Ollama serves whatever the operator has pulled, so the static
                // catalog cannot enumerate it and the live list comes from /ollama/models. Reporting
                // an empty array here would tell an operator their local provider supports nothing,
                // which is both wrong and the exact case this whole per-model design exists for.
                var declared = p.Models ?? Array.Empty<string>();
                var dynamicList = declared.Length == 0;
                var listed = useDiscovered
                    ? discovered.Keys.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray()
                    : dynamicList
                        ? new[] { p.DefaultModel }.Where(m => !string.IsNullOrWhiteSpace(m)).ToArray()
                        : declared.ToArray();

                var models = new List<Dictionary<string, object?>>();
                foreach (var model in listed)
                {
                    // What the runtime SAYS beats what the name suggests.
                    var caps = useDiscovered && discovered.TryGetValue(model, out var reported)
                        ? ModelCapabilities.FromOllama(reported)
                        : ModelCapabilityCatalog.For(p.Id, model);
                    models.Add(new Dictionary<string, object?>
                    {
                        ["model"] = model,
                        ["is_default"] = string.Equals(model, p.DefaultModel, StringComparison.OrdinalIgnoreCase),
                        ["tool_calling"] = caps.ToolCalling,
                        ["structured_output"] = caps.StructuredOutput,
                        ["streaming"] = caps.Streaming,
                        ["vision"] = caps.Vision,
                        ["embeddings"] = caps.Embeddings,
                        ["reasoning"] = caps.Reasoning,
                        ["context_window_tokens"] = caps.ContextWindowTokens,
                    });
                }
                report.Add(new Dictionary<string, object?>
                {
                    ["provider"] = p.Id,
                    ["name"] = p.Name,
                    // Per provider, and honest about which it was: "discovered" means the runtime
                    // itself reported these, "declared" means we inferred them from a name table.
                    // The UI needs the difference — a declared "no tool calling" is a guess worth
                    // second-guessing, a discovered one is fact.
                    ["source"] = useDiscovered ? "discovered" : "declared",
                    // The UI must join this with /ollama/models rather than treating the list as
                    // complete, and it can only know to do that if we say so.
                    ["models_are_dynamic"] = dynamicList,
                    ["dynamic_models_endpoint"] = dynamicList && p.Id == "ollama" ? "/ollama/models" : null,
                    ["models"] = models,
                });
            }
            return ApiJson.Ok(report);
        });

        /*
         * v3.4.0 (ADR-006) — the tool registry, inspectable.
         *
         * The harness is tool-centric and the tool inventory was the one thing about it an operator
         * could not see: which tools exist, what arguments each takes, which roles may call it, and
         * which declared tools have not been built. All of that lived in three source files that
         * never compared themselves to each other.
         *
         * Authorization is REPORTED BY ASKING THE ENFORCER. Every "may this role use this tool" cell
         * comes from ToolAuthorization.Evaluate — the same call RunTool makes — rather than from a
         * copy of its rules. The capability page taught this lesson the hard way: a report derived
         * independently of the code path it describes will eventually describe something else, and a
         * page that disagrees with the runtime is worse than no page.
         *
         * Schemas come from the tools themselves, so this doubles as the operator's view of exactly
         * what a model is offered.
         */
        app.MapGet("/tools", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            // Roles that can actually dispatch: the mission agents and specialists. Control-plane
            // identities are omitted because they are permitted everything by design, and a column
            // of unbroken "yes" tells an operator nothing.
            var roles = AntExecutionCatalog.Contracts.Keys
                .Concat(new[] { "researcher", "web", "file", "coder", "builder", "verifier" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.Ordinal).ToList();

            var registered = Queen.Tools.Tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

            var tools = new List<Dictionary<string, object?>>();
            foreach (var name in ToolInventory.Implemented.OrderBy(n => n, StringComparer.Ordinal))
            {
                // Implemented but not registered means a config gate is off — a real and common
                // state (file tools disabled), and one an operator needs distinguished from
                // "this tool does not exist", because the remedies are completely different.
                registered.TryGetValue(name, out var tool);

                var allowed = roles.Where(r => ToolAuthorization.Evaluate(r, name).Allowed)
                    .OrderBy(r => r, StringComparer.Ordinal).ToList();

                tools.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["status"] = tool is not null ? "registered" : "gated_off",
                    ["description"] = tool?.Description,
                    ["parameters"] = tool is null ? null : System.Text.Json.Nodes.JsonNode.Parse(tool.ParametersJson),
                    ["structurally_forbidden"] = ToolAuthorization.MissionAgentForbidden.Contains(name),
                    ["allowed_roles"] = allowed,
                });
            }

            // Declared-but-unbuilt tools are reported as first-class entries, not omitted. A role
            // allowed only these is authorized to dispatch nothing, and that is precisely the fact
            // an operator is trying to discover when a specialist ant runs and produces no work.
            foreach (var name in ToolInventory.Planned.OrderBy(n => n, StringComparer.Ordinal))
                tools.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["status"] = "planned",
                    ["description"] = "Referenced by an ant contract; not implemented in this build.",
                    ["parameters"] = null,
                    ["structurally_forbidden"] = false,
                    ["allowed_roles"] = AntExecutionCatalog.Contracts
                        .Where(kv => kv.Value.AllowedTools.Contains(name))
                        .Select(kv => kv.Key).OrderBy(r => r, StringComparer.Ordinal).ToList(),
                });

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["tools"] = tools,
                ["roles"] = roles,
                // Computed on every request rather than stored, so it stops being true the moment a
                // planned tool ships instead of outliving the problem it describes.
                ["roles_blocked_by_missing_tools"] =
                    ToolInventory.RolesBlockedByMissingTools(AntExecutionCatalog.Contracts),

                // v3.4.1: operator-defined tools, INCLUDING the ones this run refused to register.
                // A rejected definition is the state an operator most needs to see: it is stored, it
                // is visible in the editor, and it is not callable — which, unreported, looks
                // exactly like the tool being broken.
                ["user_tools"] = Queen.Memory.LoadToolDefinitions().Select(d =>
                {
                    var outcome = Queen.UserTools.FirstOrDefault(r =>
                        string.Equals(r.Name, d.Name, StringComparison.OrdinalIgnoreCase));
                    return new Dictionary<string, object?>
                    {
                        ["name"] = d.Name,
                        ["description"] = d.Description,
                        ["kind"] = d.Kind.ToString().ToLowerInvariant(),
                        ["enabled"] = d.Enabled,
                        // Three states, not two. Collapsing "the operator switched this off" into
                        // "rejected" was found in the browser: a disabled tool rendered as rejected
                        // with an EMPTY problem list, which is indistinguishable from a definition
                        // that failed validation — and the two have opposite remedies. One is
                        // re-enabled in a click; the other has to be rewritten.
                        ["status"] = !d.Enabled ? "disabled"
                            : outcome is { Registered: true } ? "registered" : "rejected",
                        ["problems"] = outcome?.Problems ?? (IReadOnlyList<string>)Array.Empty<string>(),
                        ["config"] = d.Config,
                        // Empty means EVERY dispatching role — the permissive default the operator
                        // chose. Reporting the empty list verbatim would read as "nobody".
                        ["allowed_roles"] = d.AllowedRoles.Count > 0 ? d.AllowedRoles : roles,
                        ["created_by"] = d.CreatedBy,
                        ["created_at"] = d.CreatedAt.ToIso(),
                    };
                }).ToList(),
                ["user_tools_enabled"] = AnthillRuntime.EnableUserTools,
                ["user_tool_allowed_hosts"] = AnthillRuntime.UserToolAllowedHosts,

                // v3.4.2: each contracted role checked against the model it is ACTUALLY routed to.
                // Reported here rather than only at startup because every mismatch fails silently
                // at runtime — a role routed to a model that cannot call tools produces a confident
                // answer that skipped every tool, which in a transcript looks like a weak model
                // rather than a misconfiguration an operator could fix in thirty seconds.
                ["model_fitness"] = Queen.Router is null
                    ? new List<Dictionary<string, object?>>()
                    : AntModelFitness.CheckAll(Queen.Router, AntExecutionCatalog.Contracts)
                        .Select(f => new Dictionary<string, object?>
                        {
                            ["role"] = f.RoleId,
                            ["provider"] = f.Provider,
                            ["model"] = f.Model,
                            ["fit"] = f.Fit,
                            ["unmet"] = f.Unmet,
                        }).ToList(),
            });
        });

        /*
         * v3.7.0 — START a conversation, and set its approval policy.
         *
         * The policy is recorded WITH ITS AUTHOR here, which is what makes a standing permission
         * valid at all: an unattributed AutoApprove or Bypass fails closed back to Ask, so an
         * endpoint that let one be set without naming who set it would produce a conversation whose
         * policy silently does nothing.
         */
        app.MapPost("/conversations", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;

            ConversationRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ConversationRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var policy = Enum.TryParse<EscalationPolicy>(body?.Policy, ignoreCase: true, out var p)
                ? p : EscalationPolicy.Ask;
            var who = CurrentUsername(ctx) ?? "operator";

            var conversation = new Conversation
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Title = (body?.Title ?? "").Trim(),
                Role = string.IsNullOrWhiteSpace(body?.Role) ? "researcher" : body!.Role!.Trim(),
                Policy = policy,
                // Attribution is written for ANY standing permission. Ask needs none — nobody has to
                // sign for the safe default.
                PolicySetBy = policy == EscalationPolicy.Ask ? null : who,
                PolicySetAt = policy == EscalationPolicy.Ask ? null : AnthillTime.NowUtc(),
            };

            Queen.Memory.SaveConversation(conversation);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["id"] = conversation.Id,
                ["policy"] = conversation.EffectivePolicy.ToString().ToLowerInvariant(),
            }, $"Conversation {conversation.Id} started.");
        });

        /*
         * Run one turn. THE call site that makes the v3.7.0 runtime real.
         *
         * The turn runs INSIDE a ConversationScope, which is what puts the escalation gate on the
         * tool dispatch path: outside a scope ConversationScope.Evaluate returns null and every gate
         * check silently passes. Without this endpoint the whole escalation mechanism was reachable
         * only from tests — which is the "no call site, no feature" rule, failed.
         */
        app.MapPost("/conversations/{id}/turns", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;

            var conversation = Queen.Memory.LoadConversation(id);
            if (conversation is null) return ApiJson.Error($"No conversation '{id}'.", "not_found");

            TurnRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<TurnRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var message = (body?.Message ?? "").Trim();
            if (message.Length == 0) return ApiJson.Error("A message is required.", "bad_request");

            var mode = string.Equals(body?.Mode, "mission", StringComparison.OrdinalIgnoreCase)
                ? ConversationMode.Mission : ConversationMode.Chat;
            var answers = body?.Answers ?? new Dictionary<string, string>();

            // Every tool call this turn makes is now gated, and every decision recorded — the same
            // decision log the transcript endpoint reads back.
            using (ConversationScope.Enter(conversation, answers, Queen.Memory.SaveEscalationDecision))
            {
                var outcome = Queen.Conversations.Run(conversation, message, mode, answers);

                return ApiJson.Ok(new Dictionary<string, object?>
                {
                    ["mode"] = outcome.Mode.ToString().ToLowerInvariant(),
                    ["started"] = outcome.Started,
                    ["mission_id"] = outcome.MissionId,
                    ["summary"] = outcome.Summary,
                    ["decision"] = outcome.Decision is null ? null : new Dictionary<string, object?>
                    {
                        ["action"] = outcome.Decision.Action,
                        ["allowed"] = outcome.Decision.Allowed,
                        ["decided_by"] = outcome.Decision.DecidedBy,
                        ["reason"] = outcome.Decision.Reason,
                    },
                }, outcome.Summary);
            }
        });

        // Cancel: marks the conversation AND signals the work it started. Reports how many live
        // pieces were signalled, so "stopped two missions" is distinguishable from "nothing running".
        app.MapPost("/conversations/{id}/cancel", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            if (Queen.Memory.LoadConversation(id) is null)
                return ApiJson.Error($"No conversation '{id}'.", "not_found");

            var stopped = Queen.Conversations.Cancel(id);
            return ApiJson.Ok(new Dictionary<string, object?> { ["signalled"] = stopped },
                stopped == 0 ? "Conversation cancelled; nothing was running."
                             : $"Conversation cancelled; {stopped} running item(s) signalled.");
        });

        /*
         * v3.7.0 — conversations, and what each one is doing.
         *
         * State is DERIVED on request, never stored. A stored status is a second thing to keep in
         * step with reality and it goes wrong exactly where an operator relies on it: a process that
         * died leaves its last write saying "running" forever.
         */
        app.MapGet("/conversations", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["conversations"] = Queen.Memory.LoadConversations().Select(c =>
                {
                    var state = ConversationStateReader.Read(Queen.Memory, c.Id);
                    return new Dictionary<string, object?>
                    {
                        ["id"] = c.Id,
                        ["title"] = c.Title,
                        ["role"] = c.Role,
                        // The EFFECTIVE policy, not the stored one. An unattributed standing
                        // permission falls back to Ask, and reporting the stored value would tell an
                        // operator they had switched approvals off when they had not.
                        ["policy"] = state.Policy.ToString().ToLowerInvariant(),
                        ["policy_set_by"] = c.PolicySetBy,
                        ["policy_attributed"] = c.PolicyIsAttributed,
                        ["cancelled"] = c.Cancelled,
                        ["mission_ids"] = c.MissionIds,
                        ["doing"] = state.Doing,
                        ["waiting_on"] = state.WaitingOn,
                        // Hoisted so a UI can highlight it without re-deriving the rule: this is the
                        // only state where nothing moves until a human acts.
                        ["needs_operator"] = state.NeedsOperator,
                        ["updated_at"] = c.UpdatedAt.ToIso(),
                    };
                }).ToList(),
            });
        });

        // One conversation, with its transcript and its decision log. The two together are the
        // whole audit: what was said, and what was permitted.
        app.MapGet("/conversations/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            var conversation = Queen.Memory.LoadConversation(id);
            if (conversation is null) return ApiJson.Error($"No conversation '{id}'.", "not_found");

            var state = ConversationStateReader.Read(Queen.Memory, id);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["id"] = conversation.Id,
                ["doing"] = state.Doing,
                ["did"] = state.Did,
                ["waiting_on"] = state.WaitingOn,
                ["needs_operator"] = state.NeedsOperator,
                ["policy"] = state.Policy.ToString().ToLowerInvariant(),
                ["mission_ids"] = conversation.MissionIds,
                ["turns"] = Queen.Memory.LoadConversationTurns(id).Select(t => new Dictionary<string, object?>
                {
                    ["ordinal"] = t.Ordinal, ["role"] = t.Role, ["content"] = t.Content,
                    ["provider"] = t.Provider, ["model"] = t.Model,
                    ["tools_offered"] = t.ToolsOffered, ["tools_called"] = t.ToolsCalled,
                    ["mission_id"] = t.MissionId, ["created_at"] = t.CreatedAt.ToIso(),
                }).ToList(),
                // Refusals included. An audit asking "did it try to do X" needs those most, because
                // they are the attempts nobody saw happen.
                ["decisions"] = Queen.Memory.LoadEscalationDecisions(id).Select(d => new Dictionary<string, object?>
                {
                    ["action"] = d.Action, ["allowed"] = d.Allowed,
                    ["policy"] = d.Policy.ToString().ToLowerInvariant(),
                    ["decided_by"] = d.DecidedBy, ["asked_directly"] = d.WasAskedDirectly,
                    ["reason"] = d.Reason, ["decided_at"] = d.DecidedAt.ToIso(),
                }).ToList(),
            });
        });

        /*
         * v3.5.0 — the mission workspaces, and what each change was based on.
         *
         * Reports CLEANED and ORPHANED workspaces alongside live ones, because the row outliving the
         * directory is the point: "what was this merged change based on" is asked long after the
         * files are gone, and a list showing only what currently exists cannot answer it.
         *
         * Orphaned is kept distinct from cleaned in the report for the same reason it is distinct in
         * the model — "we removed it" and "it vanished under us" call for different responses, and a
         * list that shows only "gone" hides the second entirely.
         */
        /*
         * v3.8.0 — durable attempts, and the ones that need a human.
         *
         * Recovery already reports abandoned work to stderr at startup, which is exactly nobody's
         * console. An attempt that MAY have left effects outside the process is, by design, not
         * automatically redeliverable — it waits for an operator who can look — and a decision that
         * waits for a human it never reaches is not a decision, it is a stall.
         */
        app.MapGet("/attempts", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            static Dictionary<string, object?> Project(Anthill.Core.Workers.TaskAttempt a) => new()
            {
                ["id"] = a.Id,
                ["task_id"] = a.TaskId,
                ["mission_id"] = a.MissionId,
                ["number"] = a.Number,
                ["worker_id"] = a.WorkerId,
                ["state"] = a.State.ToString().ToLowerInvariant(),
                ["provider"] = a.Provider,
                ["model"] = a.Model,
                ["may_have_side_effects"] = a.MayHaveSideEffects,
                // Reported rather than inferred from the state name, so the console cannot offer a
                // retry the colony would consider unsafe.
                ["safe_to_redeliver"] = a.SafeToRedeliver,
                ["failure_class"] = a.FailureClass,
                ["failure_reason"] = a.FailureReason,
                ["started_at"] = a.StartedAt.ToIso(),
                ["finished_at"] = a.FinishedAt?.ToIso(),
            };

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["recent"] = Queen.Memory.LoadRecentAttempts().Select(Project).ToList(),
                ["needs_review"] = Queen.Memory.LoadAttemptsNeedingReview().Select(Project).ToList(),
                ["worker"] = Anthill.Core.Workers.LocalWorker.Id,
            });
        });

        app.MapGet("/workspaces", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            var workspaces = Queen.Workspaces.All().Select(w => new Dictionary<string, object?>
            {
                ["id"] = w.Id,
                ["mission_id"] = w.MissionId,
                ["state"] = w.State.ToString().ToLowerInvariant(),
                ["mode"] = w.Mode,
                ["root"] = w.Root,
                ["base_revision"] = w.BaseRevision,
                ["repository_fingerprint"] = w.RepositoryFingerprint,
                ["branch"] = w.Branch,
                ["retained_by"] = w.RetainedBy,
                ["retain_reason"] = w.RetainReason,
                ["note"] = w.Note,
                // Whether cleanup may take it. Reported rather than inferred from the state name, so
                // the UI cannot draw a delete button the server would refuse.
                ["deletable"] = w.Deletable,
                ["usable"] = w.Usable,
                ["created_at"] = w.CreatedAt.ToIso(),
                ["updated_at"] = w.UpdatedAt.ToIso(),
            }).ToList();

            // What each LIVE workspace can be verified with. Detected on request rather than stored,
            // because a workspace's project types change the moment an agent adds a package.json —
            // and a stored manifest would keep describing the repository as it was when it was made.
            foreach (var entry in workspaces)
            {
                var root = entry["root"]?.ToString() ?? "";
                if (entry["usable"] is not true || root.Length == 0) continue;

                var manifest = Anthill.Core.Workspaces.WorkspaceCapabilityManifest.Detect(root);
                entry["project_types"] = manifest.ProjectTypes;
                entry["adapter_versions"] = manifest.AdapterVersions;
                // The check IDS, not the command lines. An operator needs to know what can be run;
                // publishing the argument strings would invite treating them as editable, and they
                // are declared in the repository precisely so they are not.
                entry["available_checks"] = manifest.Checks.Select(c => c.Id).ToList();
            }

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["workspaces"] = workspaces,
                ["root"] = Anthill.Core.Workspaces.MissionWorkspaceManager.Root,
            });
        });

        /*
         * v3.4.1 (ADR-006) — define a tool without a rebuild.
         *
         * Validated BEFORE it is stored, by the SAME validator the registrar uses at startup. A
         * definition accepted here and rejected at the next restart would be the worst of both
         * worlds: an operator told it worked, and a colony that quietly does not have it.
         *
         * Registration into the live registry is immediate, so the tool is usable in the next
         * mission rather than after a restart — and it is the same ToolRegistry every built-in lives
         * in. The absence of a separate path IS the feature; see Queen.BuildToolRegistry.
         */
        app.MapPost("/tools/user", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_settings"); if (auth is not null) return auth;
            if (!AnthillRuntime.EnableUserTools)
                return ApiJson.Error("User-defined tools are disabled by config.", "permission_denied");

            UserToolRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<UserToolRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null) return ApiJson.Error("A tool definition is required.", "bad_request");

            var definition = new ToolDefinition
            {
                Name = (body.Name ?? "").Trim().ToLowerInvariant(),
                Description = (body.Description ?? "").Trim(),
                Kind = ToolKinds.Parse(body.Kind),
                ParametersJson = string.IsNullOrWhiteSpace(body.Parameters)
                    ? """{"type":"object","properties":{}}""" : body.Parameters!,
                Config = body.Config ?? new Dictionary<string, string>(),
                AllowedRoles = body.AllowedRoles ?? new List<string>(),
                Enabled = body.Enabled ?? true,
            };

            var problems = UserToolRegistrar.Default().Validate(definition);
            if (problems.Count > 0)
                return ApiJson.Error($"Tool definition rejected: {string.Join("; ", problems)}",
                    "bad_request", new Dictionary<string, object?> { ["problems"] = problems });

            Queen.Memory.SaveToolDefinition(definition);
            // The WHOLE set is re-registered rather than just this one, which keeps the grant table
            // a wholesale replacement — the property that stops a since-removed definition from
            // being granted forever.
            Queen.ReloadUserTools();
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "user_tool_registered",
                $"Operator-defined tool '{definition.Name}' registered", null, "operator",
                new() { ["tool_name"] = definition.Name, ["kind"] = definition.Kind.ToString() });

            return ApiJson.Ok(new Dictionary<string, object?> { ["name"] = definition.Name },
                $"Tool '{definition.Name}' registered.");
        });

        // Revoke. DISABLING is the default because the row is evidence — a transcript that called
        // the tool stays explainable. `?purge=true` deletes outright, for one created in error.
        app.MapDelete("/tools/user/{name}", (HttpContext ctx, string name) =>
        {
            var auth = RequireAuth(ctx, "manage_settings"); if (auth is not null) return auth;

            var purge = string.Equals(ctx.Request.Query["purge"], "true", StringComparison.OrdinalIgnoreCase);
            var changed = purge
                ? Queen.Memory.DeleteToolDefinition(name)
                : Queen.Memory.SetToolDefinitionEnabled(name, false);
            if (!changed) return ApiJson.Error($"No user-defined tool named '{name}'.", "not_found");

            // Out of the LIVE registry too. Leaving it registered would keep offering a model a tool
            // whose definition is gone, and every call would fail for a reason no transcript shows.
            Queen.Tools.Unregister(name);
            Queen.ReloadUserTools();
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId,
                purge ? "user_tool_deleted" : "user_tool_disabled",
                $"Operator-defined tool '{name}' {(purge ? "deleted" : "disabled")}", null, "operator",
                new() { ["tool_name"] = name });

            return ApiJson.Ok(new Dictionary<string, object?> { ["name"] = name },
                purge ? $"Tool '{name}' deleted." : $"Tool '{name}' disabled.");
        });

        // Add or update a connection. api_key is optional on update (blank = leave the stored key
        // untouched); required the first time a provider is connected.
        app.MapPost("/providers", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_providers"); if (auth is not null) return auth;
            ProviderUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ProviderUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Provider)) return ApiJson.Error("Provider is required.", "bad_request");

            var err = Queen.Memory.UpsertProviderCredential(
                body!.Provider!, body.ApiKey, body.BaseUrl, body.Enabled ?? true, body.Label);
            if (err.Length > 0) return ApiJson.Error(err, "bad_request");
            return ApiJson.Ok(Queen.Memory.ListProviderConnections(), $"Saved {SqliteMemory.NormalizeProvider(body.Provider)} connection.");
        });

        app.MapDelete("/providers/{provider}", (HttpContext ctx, string provider) =>
        {
            var auth = RequireAuth(ctx, "manage_providers"); if (auth is not null) return auth;
            Queen.Memory.DeleteProviderCredential(provider);
            return ApiJson.Ok(Queen.Memory.ListProviderConnections(), $"Removed {SqliteMemory.NormalizeProvider(provider)} connection.");
        });

        // Fires one small live request through the real routing path (ModelRouter) to confirm the
        // stored key actually works, and records the outcome for the console to display.
        app.MapPost("/providers/{provider}/test", (HttpContext ctx, string provider) =>
        {
            var auth = RequireAuth(ctx, "manage_providers"); if (auth is not null) return auth;

            /*
             * v3.8.39 — an installed CLI agent is testable too.
             *
             * This gated on KeyedProviders, which is the set of providers holding an API KEY. An
             * agent holds none — the operator signed into the vendor's own tool — so agents failed
             * here with "Unknown provider", and an operator could ROUTE an ant to Claude Code but
             * could not check it worked first. Selecting something you cannot verify is exactly
             * when a Test button matters.
             *
             * Handled before NormalizeProvider, which lowercases and trims for the credential
             * store's benefit and has no business rewriting a namespaced agent id.
             */
            if (AgentCliCatalog.IsAgentId(provider))
            {
                if (Queen.Router is null)
                    return ApiJson.Error("Model routing is disabled for this colony.", "bad_request");

                var agent = AgentCliCatalog.ById(provider);
                if (agent is null) return ApiJson.Error($"No such agent: {provider}.", "not_found");

                /*
                 * Bounded SHORT, and built directly rather than through the router.
                 *
                 * A connection test answers one question — "can I reach this right now" — and must
                 * come back while the operator is still looking at the button. A mission turn is a
                 * different question with a legitimately different bound: a coding agent editing
                 * files runs for minutes, so the routed provider allows that.
                 *
                 * Found live: `opencode run` did not return, and this endpoint had inherited the
                 * mission-length allowance, so Test hung with the request still open on the server.
                 * Thirty seconds is long enough for any agent that is going to answer at all and
                 * short enough that a hung one reports rather than pins a request.
                 */
                // Held as IReasoningProvider, not as AgentCliProvider: `Generate` is a DEFAULT
                // INTERFACE METHOD, which C# dispatches only through the interface. Calling it on
                // the concrete type is CS1061, and the message ("does not contain a definition")
                // reads like a missing member rather than the interface rule it actually is.
                IReasoningProvider probe = new AgentCliProvider(agent, TimeSpan.FromSeconds(30));
                var agentReply = probe.Generate("Reply with the single word: OK", retries: 1);

                // Deliberately NOT recorded through SetProviderVerification. That table is the
                // credential store's view of a keyed provider, and an agent has no row in it —
                // writing one would invent a credential Anthill does not hold and never will.
                return agentReply.Ok
                    ? ApiJson.Ok(new Dictionary<string, object?>
                    {
                        ["provider"] = agent.Id,
                        ["reply"] = agentReply.Content,
                    }, $"{agent.DisplayName} answered.")
                    : ApiJson.Error(agentReply.Content, "provider_test_failed");
            }

            var p = SqliteMemory.NormalizeProvider(provider);
            if (!ProviderCatalog.KeyedProviders.Contains(p))
                return ApiJson.Error($"Unknown provider '{p}'.", "bad_request");
            if (Queen.Router is null)
                return ApiJson.Error("Model routing is disabled for this colony.", "bad_request");

            var client = Queen.Router.GetClientForProvider(p);
            var reply = client.Generate("Reply with the single word: OK", retries: 1);
            // v3.2.0: the provider's own status, not a prefix test on its prose. This also closes
            // a real hole — "<provider> returned an empty response." does not start with ERROR:,
            // so a provider that answered with nothing used to be recorded as VERIFIED.
            var ok = reply.Ok;
            Queen.Memory.SetProviderVerification(p, ok, reply.Content);
            return ok
                ? ApiJson.Ok(Queen.Memory.ListProviderConnections(), $"{p} connection verified.")
                : ApiJson.Error(reply.Content, "provider_test_failed");
        });
    }

    /// <summary>
    /// Ask Ollama which models it is holding and what each can do (/api/tags → capabilities[]).
    ///
    /// Best-effort ON PURPOSE. Ollama frequently lives on another host and is frequently down; a
    /// capabilities page that fails because a local runtime is asleep is worse than one that falls
    /// back to declared values and says so. An empty result therefore means "could not ask", never
    /// "supports nothing" — the caller distinguishes them, and the response reports which it used.
    /// </summary>
    /// <summary>
    /// The names a local Ollama host currently holds, synchronously. v3.8.33.
    ///
    /// Registered into <c>ReasoningProviders</c> so the core can resolve "which model" without owning
    /// HTTP. THROWS on failure rather than returning empty, deliberately: "the host could not be
    /// asked" and "the host has no models" need different fixes — start Ollama versus pull a model —
    /// and collapsing them into an empty list would print the wrong instruction.
    /// </summary>
    internal static IReadOnlyList<string> InstalledOllamaModels(string host)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var baseHost = (host ?? "").Trim().TrimEnd('/');
        using var resp = InternalHttp.GetAsync($"{baseHost}/api/tags", cts.Token).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();

        var body = resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        using var doc = System.Text.Json.JsonDocument.Parse(body);

        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("models", out var models)
            && models.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var m in models.EnumerateArray())
                if (m.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    names.Add(name);
        }
        return names;
    }

    private static async Task<Dictionary<string, List<string>>> DiscoverOllamaModelsAsync()
    {
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var host = AnthillRuntime.OllamaHost.TrimEnd('/');
            var resp = await InternalHttp.GetAsync($"{host}/api/tags", cts.Token);
            if (!resp.IsSuccessStatusCode) return found;

            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            var root = System.Text.Json.Nodes.JsonNode.Parse(body)?.AsObject();
            foreach (var entry in root?["models"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
            {
                var name = entry?["name"]?.GetValue<string>() ?? entry?["model"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var caps = new List<string>();
                foreach (var c in entry?["capabilities"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                {
                    var value = c?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(value)) caps.Add(value!);
                }
                found[name!] = caps;
            }
        }
        catch (Exception)
        {
            // Unreachable, slow, or a shape we do not recognise: fall back to declared. Deliberately
            // silent — this runs on every page load of a settings screen, and an operator with no
            // local runtime configured should not be reading exception noise in their logs.
        }
        return found;
    }
}
