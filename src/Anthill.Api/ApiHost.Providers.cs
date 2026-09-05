using System.Reflection;
using Anthill.SDK.Common;   // TextUtil — the workspace list's bounded mission-goal name
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
                    : $"Not installed. {AgentCliCatalog.InstallHint(s.Agent)}",
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
                    : AntModelFitness.CheckAll(Queen.Router)
                        .Select(f => new Dictionary<string, object?>
                        {
                            ["role"] = f.RoleId,
                            ["provider"] = f.Provider,
                            // The EFFECTIVE model — what a call would really use. v0.3.8.41. This
                            // reported the CONFIGURED model, which is empty when nobody has chosen
                            // one, so the console showed `ollama:` and listed capabilities that
                            // empty string lacked.
                            ["model"] = f.Model,
                            ["fit"] = f.Fit,
                            ["unmet"] = f.Unmet,
                            // Null unless the route cannot resolve at all. A consumer must show this
                            // INSTEAD of `unmet`: "choose a model" and "your model lacks structured
                            // output" are different problems, and the second is false when the first
                            // is true.
                            ["unresolved"] = f.Unresolved,
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

            var who = CurrentUsername(ctx) ?? "operator";

            // v0.3.8.48 (directive correction of the .47 behaviour): a project is NOT created per
            // conversation. The operator selects or creates one first — the UI's picker enforces
            // that flow; the API enforces the invariant. This is what stops Projects collapsing
            // into a list of one-conversation containers.
            var projectId = (body?.ProjectId ?? "").Trim();
            if (projectId.Length == 0)
                return ApiJson.Error("A conversation lives in a project. Pick one or create one first.", "project_required");
            var owner = Queen.Memory.LoadProject(projectId);
            if (owner is null) return ApiJson.Error($"No project '{projectId}'.", "not_found");
            if (owner.Archived) return ApiJson.Error($"\"{owner.Name}\" is archived. Unarchive it to start new work there.", "bad_request");

            // Policy: explicit request wins; otherwise the PROJECT's attributed default applies —
            // and it applies with the project author's attribution, because that is who made the
            // standing decision. No author anywhere = Ask. Fail closed, same rule as ever.
            var policy = Enum.TryParse<EscalationPolicy>(body?.Policy, ignoreCase: true, out var p)
                ? p : owner.EffectiveDefaultPolicy;
            var policyBy = Enum.TryParse<EscalationPolicy>(body?.Policy, ignoreCase: true, out _)
                ? who : owner.DefaultPolicyBy;

            var conversation = new Conversation
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Title = (body?.Title ?? "").Trim(),
                Role = string.IsNullOrWhiteSpace(body?.Role) ? "researcher" : body!.Role!.Trim(),
                Policy = policy,
                ProjectId = projectId,
                // Attribution is written for ANY standing permission. Ask needs none — nobody has to
                // sign for the safe default.
                PolicySetBy = policy == EscalationPolicy.Ask ? null : policyBy,
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

            // v0.3.8.55 (fourth field round): the working-directory gate is GONE, by the same
            // operator who asked for it in the third. A pathless project no longer refuses the
            // turn — it stands in ANTHILL's own source checkout by default (direct source
            // access; ConversationRunner.ProjectDirectory is the one resolution), and the
            // operator's explicit choice takes over the moment one is set from the Files tab.

            // v0.3.8.52 (field report): a conversation born EMPTY — the project page's New
            // Conversation button creates one before anything is said — takes its title from the
            // first thing said in it. The console titles only at creation, so without this the
            // tracker shows an unnamed row forever.
            if (string.IsNullOrWhiteSpace(conversation.Title))
            {
                conversation = conversation with
                {
                    Title = message.Length <= 48 ? message : message[..48],
                    UpdatedAt = AnthillTime.NowUtc(),
                };
                Queen.Memory.SaveConversation(conversation);
            }

            var mode = string.Equals(body?.Mode, "mission", StringComparison.OrdinalIgnoreCase)
                ? ConversationMode.Mission : ConversationMode.Chat;
            var answers = body?.Answers ?? new Dictionary<string, string>();

            static Dictionary<string, object?> OutcomePayload(Anthill.Core.Conversations.ConversationOutcome outcome) => new()
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
            };

            // v0.3.8.47: attachments — text only, capped, and the caps are SPOKEN. A file the
            // prompt transport could never carry is refused here, not stored as a lie.
            var files = new List<(string Filename, string Content)>();
            foreach (var a in body?.Attachments ?? new List<AttachmentBody>())
            {
                var name = (a.Filename ?? "file.txt").Trim();
                var content = a.Content ?? "";
                if (files.Count >= 8) return ApiJson.Error("At most 8 attachments per message.", "bad_request");
                if (content.Length > 262_144)
                    return ApiJson.Error($"\"{name}\" is too large — attachments are capped at 256 KB of text.", "bad_request");
                if (content.Contains('\0'))
                    return ApiJson.Error($"\"{name}\" looks binary. The conversation carries text; attach text files.", "bad_request");
                files.Add((name, content));
            }
            var attachments = files.Count == 0 ? null : files;

            /*
             * v0.3.8.44 — the streamed turn. Same runner, same recording, same outcome; the only
             * difference is that content deltas travel to the client AS the provider produces
             * them, as SSE frames, with the final outcome as the terminal `done` event. The
             * client's disconnect token is bound into ModelCallScope, so closing the tab or
             * aborting the fetch aborts the model call itself — cancellation reaches the
             * provider, not merely the animation.
             */
            if (body?.Stream == true)
            {
                ctx.Response.Headers.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";

                void Frame(string @event, string json)
                {
                    ctx.Response.WriteAsync($"event: {@event}\ndata: {json}\n\n", ctx.RequestAborted)
                        .GetAwaiter().GetResult();
                    ctx.Response.Body.FlushAsync(ctx.RequestAborted).GetAwaiter().GetResult();
                }

                using (Anthill.SDK.Reasoning.ModelCallScope.Enter(ctx.RequestAborted))
                using (ConversationScope.Enter(conversation, answers, Queen.Memory.SaveEscalationDecision))
                {
                    // v0.3.8.58 — NO `delta` FRAMES, because there is no longer a model answering a
                    // chat turn to stream from. Every message is a mission: this call returns once
                    // the mission id exists, and the colony's answer arrives in the transcript when
                    // the work settles.
                    //
                    // The endpoint is kept rather than removed so the console's existing fetch keeps
                    // working, and it emits only what is true — a single terminal `done`. Faking a
                    // trickle by chunking the summary would be an animation pretending to be
                    // progress, which is exactly what the eternal spinner of v0.3.8.42 was.
                    var outcome = Queen.Conversations.Run(conversation, message, mode, answers,
                        attachments: attachments);
                    try { Frame("done", System.Text.Json.JsonSerializer.Serialize(OutcomePayload(outcome))); }
                    catch { /* client gone before the end — the turn is recorded regardless */ }
                }
                return Microsoft.AspNetCore.Http.Results.Empty;
            }

            // Every tool call this turn makes is now gated, and every decision recorded — the same
            // decision log the transcript endpoint reads back.
            using (ConversationScope.Enter(conversation, answers, Queen.Memory.SaveEscalationDecision))
            {
                var outcome = Queen.Conversations.Run(conversation, message, mode, answers, attachments: attachments);
                return ApiJson.Ok(OutcomePayload(outcome), outcome.Summary);
            }
        });

        // v0.3.8.48: change a conversation's approval policy in place — the selector in the chat
        // header. Attributed on every change; Ask clears attribution (the safe default needs no
        // signature). This changes which OPERATOR PROMPTS appear; it never touches authentication,
        // role permissions, workspace boundaries, capability gates, or verification requirements.
        app.MapPost("/conversations/{id}/policy", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            var conversation = Queen.Memory.LoadConversation(id);
            if (conversation is null) return ApiJson.Error($"No conversation '{id}'.", "not_found");
            ConversationRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ConversationRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (!Enum.TryParse<EscalationPolicy>(body?.Policy, ignoreCase: true, out var policy))
                return ApiJson.Error("Policy must be ask, autoapprove, or bypass.", "bad_request");

            var who = CurrentUsername(ctx) ?? "operator";
            Queen.Memory.SaveConversation(conversation with
            {
                Policy = policy,
                PolicySetBy = policy == EscalationPolicy.Ask ? null : who,
                PolicySetAt = policy == EscalationPolicy.Ask ? null : AnthillTime.NowUtc(),
                UpdatedAt = AnthillTime.NowUtc(),
            });
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["policy"] = policy.ToString().ToLowerInvariant(),
                ["set_by"] = policy == EscalationPolicy.Ask ? null : who,
            }, policy switch
            {
                EscalationPolicy.Ask => "Manual approval — the colony asks before side effects.",
                EscalationPolicy.AutoApprove => "Automatically approve — eligible side effects proceed, every decision recorded.",
                _ => "Skip all approvals — prompts are skipped; security gates and verification still apply.",
            });
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

            // v0.3.8.46: ?q= filters server-side over titles and turn content — the same store
            // the transcripts live in, so the search box finds exactly what is recorded.
            var q = ctx.Request.Query["q"].ToString();
            var list = string.IsNullOrWhiteSpace(q)
                ? Queen.Memory.LoadConversations()
                : Queen.Memory.SearchConversations(q);

            // v0.3.8.52 (field report: "cannot tell what conversations go where") — the project
            // NAME rides every row. One load for the whole list, not one per conversation.
            var projectNames = Queen.Memory.LoadProjects()
                .ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["conversations"] = list.Select(c =>
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
                        ["pinned"] = c.Pinned,
                        // v0.3.8.51, found live: neither the list nor the detail carried the
                        // project link — the conversation→project chain the whole files pane and
                        // gates affordance stand on was invisible to every UI reader.
                        ["project_id"] = c.ProjectId,
                        ["project_name"] = c.ProjectId is not null && projectNames.TryGetValue(c.ProjectId, out var pn) ? pn : null,
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

        /*
         * v0.3.8.47 — projects. One per conversation (created at conversation start), or made by
         * hand here with a name, a markdown purpose, and an optional working-directory path. The
         * purpose travels into the project's conversations as standing context; the path is
         * recorded and given to the model as context — no surface claims deeper wiring than that.
         */
        app.MapGet("/projects", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["projects"] = Queen.Memory.LoadProjects().Select(p =>
                {
                    var convs = Queen.Memory.LoadProjectConversations(p.Id);
                    return new Dictionary<string, object?>
                    {
                        ["id"] = p.Id, ["name"] = p.Name, ["description_md"] = p.DescriptionMd,
                        ["path"] = p.Path, ["archived"] = p.Archived,
                        ["conversations"] = convs.Count,
                        ["missions"] = convs.Sum(c => c.MissionIds.Count),
                        ["updated_at"] = p.UpdatedAt.ToIso(),
                    };
                }).ToList(),
            });
        });

        app.MapPost("/projects", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            ProjectRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ProjectRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var name = (body?.Name ?? "").Trim();
            if (name.Length == 0) return ApiJson.Error("A project needs a name.", "bad_request");

            var project = new Anthill.Core.Projects.Project
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = name,
                DescriptionMd = (body?.DescriptionMd ?? "").Trim(),
                Path = string.IsNullOrWhiteSpace(body?.Path) ? null : body!.Path!.Trim(),
            };
            Queen.Memory.SaveProject(project);
            return ApiJson.Ok(new Dictionary<string, object?> { ["id"] = project.Id },
                $"Project \"{project.Name}\" created.");
        });

        app.MapPatch("/projects/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            ProjectRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ProjectRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            // v0.3.8.48: changing the default policy is a standing decision — recorded with the
            // caller's name and the moment. Unchanged fields keep their existing attribution.
            var who = CurrentUsername(ctx) ?? "operator";
            var policyChanged = Enum.TryParse<EscalationPolicy>(body?.DefaultPolicy, ignoreCase: true, out var newPolicy);

            // v0.3.8.52 (third field round): setting the working directory is the operator's own
            // explicit act, and the directory is CREATED for them when new — the suggested
            // per-project tree is new by definition, and "set it, then be told it does not
            // exist" would be the form arguing with itself.
            var newPath = string.IsNullOrWhiteSpace(body?.Path) ? null : body!.Path!.Trim();
            if (newPath is not null && !Directory.Exists(newPath))
            {
                try { Directory.CreateDirectory(newPath); }
                catch (Exception ex)
                { return ApiJson.Error($"Could not create {newPath}: {ex.Message}", "bad_request"); }
            }

            Queen.Memory.SaveProject(project with
            {
                Name = string.IsNullOrWhiteSpace(body?.Name) ? project.Name : body!.Name!.Trim(),
                DescriptionMd = body?.DescriptionMd ?? project.DescriptionMd,
                Path = body?.Path is null ? project.Path
                     : (string.IsNullOrWhiteSpace(body.Path) ? null : body.Path.Trim()),
                Archived = body?.Archived ?? project.Archived,
                DefaultPolicy = policyChanged ? newPolicy : project.DefaultPolicy,
                DefaultPolicyBy = policyChanged ? (newPolicy == EscalationPolicy.Ask ? null : who) : project.DefaultPolicyBy,
                DefaultPolicyAt = policyChanged ? (newPolicy == EscalationPolicy.Ask ? null : AnthillTime.NowUtc()) : project.DefaultPolicyAt,
                DefaultProvider = body?.DefaultProvider ?? project.DefaultProvider,
                DefaultModel = body?.DefaultModel ?? project.DefaultModel,
                UpdatedAt = AnthillTime.NowUtc(),
            });
            return ApiJson.Ok(null, "Project updated.");
        });

        /*
         * v0.3.8.51 (field report) — DIRECTORY GATES. The operator opens a path for a project's
         * colony the same way they open the approval gate: explicitly, attributed, revocable.
         * Each grant becomes agent-CLI reach (--add-dir) for that project's missions and nothing
         * else does. Mirrors the shape of Anthill's approval gates on purpose.
         */
        app.MapGet("/projects/{id}/grants", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            if (Queen.Memory.LoadProject(id) is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["grants"] = Queen.Memory.LoadProjectGrants(id).Select(g => new Dictionary<string, object?>
                {
                    ["id"] = g.Id, ["path"] = g.Path,
                    ["granted_by"] = g.GrantedBy, ["granted_at"] = g.GrantedAt.ToIso(),
                }).ToList(),
                ["note"] = "Each grant is a directory gate: the project's colony may reach this path. Revoking closes it.",
            });
        });

        app.MapPost("/projects/{id}/grants", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_settings"); if (auth is not null) return auth;
            if (Queen.Memory.LoadProject(id) is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            Dictionary<string, string?>? body;
            try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string?>>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var raw = (body?.GetValueOrDefault("path") ?? "").Trim();
            if (raw.Length == 0) return ApiJson.Error("A directory path is required.", "bad_request");

            // The gate opens onto something REAL and NAMED EXACTLY: an absolute, existing
            // directory. A relative path would silently mean "relative to wherever the host
            // happens to run", which is not what anyone approved.
            string full;
            try { full = System.IO.Path.GetFullPath(raw); }
            catch { return ApiJson.Error("That is not a usable path.", "bad_request"); }
            if (!System.IO.Path.IsPathRooted(raw))
                return ApiJson.Error("Directory gates take absolute paths — say exactly which directory opens.", "bad_request");
            if (!Directory.Exists(full))
                return ApiJson.Error($"'{full}' does not exist or is not a directory.", "bad_request");

            var who = CurrentUsername(ctx) ?? "operator";
            var grant = new Anthill.Core.Memory.ProjectGrant(
                Guid.NewGuid().ToString("N")[..12], id, full) { GrantedBy = who };
            Queen.Memory.SaveProjectGrant(grant);
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "directory_gate_opened",
                $"Directory gate opened for project '{id}': {full} (by {who}).", antName: who);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["id"] = grant.Id, ["path"] = full,
            }, $"The colony may now reach {full} for this project.");
        });

        /* ---- PER-PROJECT MODEL ROUTING. v0.3.8.124 -----------------------------------------------
           The Ant Inspector page is gone and routing is a project decision now. A project's PRIORITY
           model rides on `PATCH /projects/{id}` (`default_provider` / `default_model` — persisted
           since .48, read by nothing until .124); these two routes carry the per-ROLE half.

           EVERY ROUTE THE PAGE OFFERS COMES BACK FROM HERE, including the colony's own, because the
           console must be able to show what a role would use when the project says nothing about it.
           A page that showed only the overrides would leave an operator unable to tell "inherits the
           colony's llama3.3" from "unrouted", which are very different states. */
        app.MapGet("/projects/{id}/routes", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_models"); if (auth is not null) return auth;
            if (Queen.Memory.LoadProject(id) is not { } project)
                return ApiJson.Error($"No project '{id}'.", "not_found");

            var overrides = Queen.Memory.LoadProjectRoutes(id);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["project_id"] = project.Id,
                // The project's own priority — the "use this everywhere in this project" decision.
                ["priority_provider"] = project.DefaultProvider ?? "",
                ["priority_model"] = project.DefaultModel ?? "",
                ["priority_active"] = !string.IsNullOrWhiteSpace(project.DefaultProvider)
                                   && !string.IsNullOrWhiteSpace(project.DefaultModel),
                ["roles"] = AnthillRuntime.RoutableRoles.Select(role =>
                {
                    var over = overrides.TryGetValue(role, out var r) ? r : default;
                    var inherited = Queen.Router?.RoleRoute(role) ?? ("", "");
                    return new Dictionary<string, object?>
                    {
                        ["role"] = role,
                        ["overridden"] = overrides.ContainsKey(role),
                        ["provider"] = over.Provider ?? "",
                        ["model"] = over.Model ?? "",
                        // What this role uses when the project stays silent. Shown so "inherits" is
                        // a legible state rather than a blank the operator has to interpret.
                        ["colony_provider"] = inherited.Provider,
                        ["colony_model"] = inherited.Model,
                    };
                }).ToList(),
                ["colony_priority_active"] = AnthillRuntime.HasModelPriority,
            });
        });

        app.MapPost("/projects/{id}/routes", async (HttpContext ctx, string id) =>
        {
            // `manage_models` — the same permission `POST /routes/{role}` takes for the colony-wide
            // equivalent. A narrower scope is not a lesser act: this decides which model does the
            // work for everything in the project.
            var auth = RequireAuth(ctx, "manage_models"); if (auth is not null) return auth;
            if (Queen.Memory.LoadProject(id) is null) return ApiJson.Error($"No project '{id}'.", "not_found");

            Dictionary<string, string?>? body;
            try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string?>>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var role = (body?.GetValueOrDefault("role") ?? "").Trim();
            if (role.Length == 0) return ApiJson.Error("A role is required.", "bad_request");
            if (!AnthillRuntime.RoutableRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                return ApiJson.Error(
                    $"'{role}' is not a routable role. Known: {string.Join(", ", AnthillRuntime.RoutableRoles)}.",
                    "bad_request");

            var provider = (body?.GetValueOrDefault("provider") ?? "").Trim();
            var model = (body?.GetValueOrDefault("model") ?? "").Trim();
            var who = CurrentUsername(ctx) ?? "operator";

            // CLEARING IS AN EMPTY PROVIDER, and it means "inherit the colony's route" rather than
            // "this role has no model". Those are different states and only one of them is
            // expressible: a project cannot un-route a role, only decline to override it.
            if (provider.Length == 0 && model.Length == 0)
            {
                Queen.Memory.DeleteProjectRoute(id, role);
                return ApiJson.Ok(new Dictionary<string, object?> { ["role"] = role, ["overridden"] = false },
                    $"'{role}' follows the colony's route again in this project.");
            }

            // Both halves or neither — the rule `HasModelPriority` applies colony-wide, applied here
            // so a half-filled form cannot route an ant at a provider with no model.
            if (provider.Length == 0 || model.Length == 0)
                return ApiJson.Error(
                    "A route needs both a provider and a model. Clear both to inherit the colony's route.",
                    "bad_request");

            Queen.Memory.SaveProjectRoute(id, role, provider, model, who);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["role"] = role, ["overridden"] = true, ["provider"] = provider, ["model"] = model,
            }, $"In this project, '{role}' now runs on {provider}:{model}.");
        });

        app.MapDelete("/projects/{id}/grants/{grantId}", (HttpContext ctx, string id, string grantId) =>
        {
            var auth = RequireAuth(ctx, "manage_settings"); if (auth is not null) return auth;
            var existing = Queen.Memory.LoadProjectGrants(id).FirstOrDefault(g => g.Id == grantId);
            if (existing is null) return ApiJson.Error("No such grant.", "not_found");
            Queen.Memory.DeleteProjectGrant(grantId);
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "directory_gate_closed",
                $"Directory gate closed for project '{id}': {existing.Path} (by {CurrentUsername(ctx) ?? "operator"}).",
                antName: CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(null, $"The gate to {existing.Path} is closed.");
        });

        /*
         * v0.3.8.52 (field report) — BROWSE for a working directory. The files pane's "set it
         * here" form asked the operator to TYPE an absolute path from memory; the Browse button
         * needs to open a picker. Two shapes, one endpoint: the desktop shell opens the real OS
         * folder dialog (a WebView2 host bridge — see ShellForm), and every browser shape uses
         * this HOST-side directory listing, because (a) a web page can never learn an absolute
         * path from the browser's own picker, by design, and (b) in Docker/LXC the working
         * directory lives on the SERVER, so the server's tree is the only one worth browsing.
         *
         * Gated on run_mission — exactly the permission that may PATCH the path this browser
         * exists to find; an operator who can set any path can already see anything this lists.
         * Directories only, hidden and system entries skipped, unreadable branches refused with
         * the reason rather than rendered empty.
         */
        app.MapGet("/fs/dirs", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            var q = ctx.Request.Query["path"].FirstOrDefault() ?? "";
            string path;
            try
            {
                path = string.IsNullOrWhiteSpace(q)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Path.GetFullPath(q.Trim());
            }
            catch { return ApiJson.Error("Not a valid path.", "bad_request"); }
            if (!Directory.Exists(path)) return ApiJson.Error("Not a directory on this host.", "not_found");

            var dirs = new List<Dictionary<string, object?>>();
            try
            {
                foreach (var d in Directory.EnumerateDirectories(path))
                {
                    var name = Path.GetFileName(d);
                    if (name.StartsWith('.')) continue;
                    try
                    {
                        if ((File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                            continue;
                    }
                    catch { continue; }   // an unreadable entry is skipped, not fatal
                    dirs.Add(new() { ["name"] = name, ["path"] = d });
                }
            }
            catch (UnauthorizedAccessException)
            {
                return ApiJson.Error("Anthill's user cannot read that directory.", "forbidden");
            }
            dirs.Sort((a, b) => string.Compare(
                (string?)a["name"], (string?)b["name"], StringComparison.OrdinalIgnoreCase));

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["parent"] = Path.GetDirectoryName(path),
                ["home"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                // Windows offers its ready drives; elsewhere the one root there is.
                ["roots"] = OperatingSystem.IsWindows()
                    ? DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name).Cast<object?>().ToList()
                    : new List<object?> { "/" },
                ["dirs"] = dirs,
            });
        });

        /*
         * v0.3.8.51 (field report) — THE FILES PANE: browse, read, and edit the project's working
         * tree from chat, side by side with the conversation. Every path is JAILED to the
         * project's own root (its Path, else the colony workspace root): resolved fully, then
         * required to stay inside. Reads are capped and text-only; writes are operator actions —
         * attributed, audited, and refused on binary or oversize content.
         */
        // The files pane's jail helpers, local to this map so they cannot be reached around.
        static (string? Root, string? Error) ProjectFileRoot(Anthill.Core.Projects.Project project)
        {
            // v0.3.8.55 (fourth field round): a pathless project no longer errors — it shows
            // ANTHILL's own source checkout, the same default the chat lane now stands in
            // (direct source access, primary until the operator sets a directory; the Dir
            // button changes it any time). Only a colony with NO source checkout — an installed
            // binary — still has nothing to show and says so.
            if (string.IsNullOrWhiteSpace(project.Path))
                return Anthill.Core.Projects.ProjectRoots.ColonySource() is { } source
                    ? (Path.GetFullPath(source), null)
                    : (null, "This project has no working directory yet, and this ANTHILL runs "
                           + "from an installed binary with no source checkout to default to — "
                           + "set a directory below.");
            if (!Directory.Exists(project.Path))
                return (null, $"The working directory {project.Path} does not exist — re-set or correct it below.");
            return (Path.GetFullPath(project.Path), null);
        }
        // v0.3.8.59 (PLAN.md §1b S1) — through the ONE resolver, not a second copy of the rule.
        //
        // This helper is the P0. It asked `full.StartsWith(root, StringComparison.Ordinal)` with no
        // separator, so a project rooted at /srv/project served `../project-secret/key.txt` — which
        // normalises to /srv/project-secret/key.txt, a SIBLING whose name begins with the root
        // string. By comparison time the `..` is gone, so nothing about the path looks like
        // traversal. It fed every Files-pane route: list, read, create and edit.
        //
        // It also never resolved links, and — separately from the runtime write flags, which these
        // endpoints do not consult — that made the READ route escapable on its own.
        //
        // The leading-separator strip stays: the pane sends browser-style paths, and `/etc/passwd`
        // arriving as an absolute path must be read as "the project's own /etc/passwd" rather than
        // handed to a resolver that would correctly refuse it with a confusing message about roots.
        static (string Full, string? Error) JailedPath(string root, string relative)
        {
            var decision = Anthill.Core.Security.PathContainment.Resolve(
                root, relative.Replace('\\', '/').TrimStart('/'));

            return decision.Allowed
                ? (decision.Path, null)
                : (decision.Path, "That path escapes the project's directory.");
        }
        // v0.3.8.52 (third field round): "is this directory ITSELF the repo toplevel" — the
        // question the git badge and the commit gate both ask, so it is answered once.
        static bool PathsEqual(string a, string b) =>
            string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        app.MapGet("/projects/{id}/files", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");
            var (full, jailError) = JailedPath(root!, ctx.Request.Query["path"].FirstOrDefault() ?? "");
            if (jailError is not null) return ApiJson.Error(jailError, "forbidden");
            if (!Directory.Exists(full)) return ApiJson.Error("Not a directory.", "not_found");

            var entries = new DirectoryInfo(full).EnumerateFileSystemInfos()
                .Where(e => e.Name != ".git")
                .OrderBy(e => e is FileInfo)                       // directories first
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .Select(e => new Dictionary<string, object?>
                {
                    ["name"] = e.Name,
                    ["dir"] = e is DirectoryInfo,
                    ["size"] = e is FileInfo f ? f.Length : 0,
                }).ToList();
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["root"] = root, ["path"] = Path.GetRelativePath(root!, full).Replace('\\', '/'),
                // v0.3.8.55: true when this root is the ANTHILL-source DEFAULT rather than the
                // operator's own choice — the crumb labels it so nobody mistakes the colony's
                // checkout for a directory they picked.
                ["default_root"] = string.IsNullOrWhiteSpace(project.Path),
                ["entries"] = entries,
            });
        });

        app.MapGet("/projects/{id}/file", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");
            var (full, jailError) = JailedPath(root!, ctx.Request.Query["path"].FirstOrDefault() ?? "");
            if (jailError is not null) return ApiJson.Error(jailError, "forbidden");
            if (!File.Exists(full)) return ApiJson.Error("No such file.", "not_found");
            var info = new FileInfo(full);
            if (info.Length > 512 * 1024)
                return ApiJson.Error($"File is {info.Length / 1024} KB — the editor caps at 512 KB.", "too_large");
            var content = File.ReadAllText(full);
            if (content.Contains('\0'))
                return ApiJson.Error("Binary file — the editor is text-only.", "binary");
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["path"] = Path.GetRelativePath(root!, full).Replace('\\', '/'),
                ["content"] = content,
            });
        });

        // v0.3.8.51 second round: CREATE — a new empty file or folder, jailed and audited. The
        // pane must be able to grow a tree, not only read one.
        app.MapPost("/projects/{id}/files", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "apply_patch"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            Dictionary<string, System.Text.Json.JsonElement>? body;
            try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var rel = body?.GetValueOrDefault("path").GetString() ?? "";
            var isDir = body is not null && body.TryGetValue("dir", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(rel)) return ApiJson.Error("A path is required.", "bad_request");

            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");
            var (full, jailError) = JailedPath(root!, rel);
            if (jailError is not null) return ApiJson.Error(jailError, "forbidden");
            if (File.Exists(full) || Directory.Exists(full))
                return ApiJson.Error("Something already exists at that path.", "conflict");

            var who = CurrentUsername(ctx) ?? "operator";
            if (isDir) Directory.CreateDirectory(full);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "");
            }
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "operator_file_created",
                $"Operator {who} created {(isDir ? "folder" : "file")} {rel} in project '{project.Name}'.", antName: who);
            return ApiJson.Ok(null, $"Created {rel}.");
        });

        app.MapPut("/projects/{id}/file", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "apply_patch"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            Dictionary<string, string?>? body;
            try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string?>>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var rel = body?.GetValueOrDefault("path") ?? "";
            var content = body?.GetValueOrDefault("content");
            if (content is null) return ApiJson.Error("content is required.", "bad_request");
            if (content.Length > 512 * 1024) return ApiJson.Error("The editor caps at 512 KB.", "too_large");
            if (content.Contains('\0')) return ApiJson.Error("Binary content is refused.", "bad_request");

            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");
            var (full, jailError) = JailedPath(root!, rel);
            if (jailError is not null) return ApiJson.Error(jailError, "forbidden");
            if (!File.Exists(full)) return ApiJson.Error("No such file — the editor edits existing files.", "not_found");

            var who = CurrentUsername(ctx) ?? "operator";
            File.WriteAllText(full, content);
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "operator_file_edited",
                $"Operator {who} edited {rel} in project '{project.Name}' via the files pane.", antName: who);
            return ApiJson.Ok(null, $"Saved {rel}.");
        });

        /*
         * v0.3.8.51 third round — GIT AWARENESS ("we need to make the colony aware whether the
         * project is a git repo or just a regular folder"). The files pane shows what the working
         * directory IS — repo on a branch with dirty files, or plain folder — and the operator can
         * commit from it. Colony-side commits ride the patch-apply path (Queen.CommitAppliedPatch)
         * under the gates that permit them; these endpoints are the OPERATOR's half.
         */
        app.MapGet("/projects/{id}/repo", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");

            // v0.3.8.52 (third field round): the git check speaks for THE PROJECT'S OWN TREE.
            // A project directory nested inside a larger repository (a fresh tree under a
            // workspace root that lives in the ANTHILL checkout, say) used to report the
            // ENCLOSING repo's branch — a branch the operator never chose, on a tree the
            // project does not track. Nested ⇒ plain folder, with the enclosure named.
            var top = Anthill.Core.Projects.RepoOps.TopLevel(root!);
            if (top is not null && !PathsEqual(top, root!))
                return ApiJson.Ok(new Dictionary<string, object?>
                {
                    ["root"] = root,
                    ["is_repo"] = false,
                    ["branch"] = null,
                    ["dirty_count"] = 0,
                    ["dirty"] = new List<Dictionary<string, object?>>(),
                    ["last_commit"] = null,
                    ["note"] = $"This directory sits inside the repository at {top} — the project "
                             + "tracks only its own tree, so git operations here are disabled.",
                });

            var state = Anthill.Core.Projects.RepoOps.Describe(root);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["root"] = root,
                ["is_repo"] = state.IsRepo,
                ["branch"] = state.Branch,
                ["dirty_count"] = state.DirtyCount,
                ["dirty"] = state.Dirty.Take(100).Select(d => new Dictionary<string, object?>
                    { ["status"] = d.Status, ["path"] = d.Path }).ToList(),
                ["last_commit"] = state.LastCommit,
                ["note"] = state.IsRepo ? null : (state.Error ?? "This directory is a plain folder, not a git repository."),
            });
        });

        /*
         * v0.3.8.52 (fourth field round) — MAKE it a repo: the operator's explicit act from the
         * files pane, offered only when the working directory is not one already. apply_patch
         * gate, same as commit: both mutate the repository state of the operator's tree.
         */
        app.MapPost("/projects/{id}/repo/init", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "apply_patch"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");

            var existingTop = Anthill.Core.Projects.RepoOps.TopLevel(root!);
            if (existingTop is not null && PathsEqual(existingTop, root!))
                return ApiJson.Error("This directory is already a git repository.", "bad_request");

            var (ok, output) = Anthill.Core.Projects.RepoOps.Init(root!);
            var who = CurrentUsername(ctx) ?? "operator";
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId,
                ok ? "repo_initialized" : "repo_init_failed",
                ok ? $"Operator {who} initialized a git repository in {root} (project '{project.Name}')."
                   : $"git init failed in {root}: {output}",
                antName: who);
            return ok
                ? ApiJson.Ok(null, "Repository created — the badge and commit controls are live now.")
                : ApiJson.Error($"git init failed: {output}", "bad_request");
        });

        // The uncommitted diff for ONE file — what sits between HEAD and the working tree. The
        // editor's "Changes" view leads with this when the project is a repo.
        app.MapGet("/projects/{id}/repo/diff", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");
            var rel = ctx.Request.Query["path"].FirstOrDefault() ?? "";
            var (_, jailError) = JailedPath(root!, rel);
            if (jailError is not null) return ApiJson.Error(jailError, "forbidden");
            var state = Anthill.Core.Projects.RepoOps.Describe(root);
            if (!state.IsRepo) return ApiJson.Error("Not a git repository.", "bad_request");
            var (ok, output) = Anthill.Core.Projects.RepoOps.DiffFile(root!,
                rel.Replace('\\', '/').TrimStart('/'));
            if (!ok) return ApiJson.Error($"git diff failed: {output}", "bad_request");
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["path"] = rel,
                ["diff"] = output.Length > 200_000 ? output[..200_000] + "\n… (truncated)" : output,
            });
        });

        /*
         * v0.3.8.52 — THE COMMIT TRAIN, GitHub-style: every branch is selectable and every file's
         * recent commits are one click away, without ever checking anything out. Branch and hash
         * inputs are validated ref-shaped/hash-shaped before git sees them; paths ride the same
         * jail as everything else in this pane.
         */
        app.MapGet("/projects/{id}/repo/branches", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");
            var (current, branches) = Anthill.Core.Projects.RepoOps.Branches(root!);
            if (current is null && branches.Count == 0)
                return ApiJson.Error("Not a git repository.", "bad_request");
            return ApiJson.Ok(new Dictionary<string, object?>
                { ["current"] = current, ["branches"] = branches });
        });

        app.MapGet("/projects/{id}/repo/log", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");
            var rel = (ctx.Request.Query["path"].FirstOrDefault() ?? "").Replace('\\', '/').TrimStart('/');
            if (rel.Length > 0)
            {
                var (_, jailError) = JailedPath(root!, rel);
                if (jailError is not null) return ApiJson.Error(jailError, "forbidden");
            }
            var branch = ctx.Request.Query["branch"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(branch) && !Anthill.Core.Projects.RepoOps.SafeRef(branch!))
                return ApiJson.Error("That is not a branch name.", "bad_request");
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var n) ? n : 20;
            var commits = Anthill.Core.Projects.RepoOps.Log(root!, branch, rel, limit);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["path"] = rel, ["branch"] = branch,
                ["commits"] = commits.Select(c => new Dictionary<string, object?>
                {
                    ["hash"] = c.Hash, ["author"] = c.Author,
                    ["time"] = c.Time, ["subject"] = c.Subject,
                }).ToList(),
            });
        });

        app.MapGet("/projects/{id}/repo/show", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");
            var rel = (ctx.Request.Query["path"].FirstOrDefault() ?? "").Replace('\\', '/').TrimStart('/');
            if (rel.Length > 0)
            {
                var (_, jailError) = JailedPath(root!, rel);
                if (jailError is not null) return ApiJson.Error(jailError, "forbidden");
            }
            var hash = ctx.Request.Query["hash"].FirstOrDefault() ?? "";
            var (ok, output) = Anthill.Core.Projects.RepoOps.ShowCommit(root!, hash, rel);
            if (!ok) return ApiJson.Error(output, "bad_request");
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["hash"] = hash, ["path"] = rel,
                ["diff"] = output.Length > 200_000 ? output[..200_000] + "\n… (truncated)" : output,
            });
        });

        app.MapPost("/projects/{id}/repo/commit", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "apply_patch"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            Dictionary<string, System.Text.Json.JsonElement>? body;
            try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var message = body?.GetValueOrDefault("message").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(message)) return ApiJson.Error("A commit message is required.", "bad_request");
            var (root, error) = ProjectFileRoot(project);
            if (error is not null) return ApiJson.Error(error, "bad_request");
            // Same scoping as the badge (third field round): commits go to the project's OWN
            // repo only — never to a repository that merely encloses the project directory.
            var commitTop = Anthill.Core.Projects.RepoOps.TopLevel(root!);
            if (commitTop is null || !PathsEqual(commitTop, root!))
                return ApiJson.Error(commitTop is null
                    ? "This project's directory is not a git repository."
                    : $"This directory sits inside the repository at {commitTop} — the project tracks "
                    + "only its own tree, so committing from here is disabled.", "bad_request");
            var paths = new List<string>();
            if (body is not null && body.TryGetValue("paths", out var pv)
                && pv.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var el in pv.EnumerateArray())
                    if (el.GetString() is { Length: > 0 } s)
                    {
                        var (_, jailErr) = JailedPath(root!, s);
                        if (jailErr is not null) return ApiJson.Error(jailErr, "forbidden");
                        paths.Add(s.Replace('\\', '/').TrimStart('/'));
                    }
            var who = CurrentUsername(ctx) ?? "operator";
            var (ok, output) = Anthill.Core.Projects.RepoOps.Commit(root!, paths, message.Trim(), who);
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, ok ? "operator_commit" : "operator_commit_failed",
                ok ? $"Operator {who} committed in project '{project.Name}': {message.Split('\n')[0]}"
                   : $"Operator {who} commit failed in project '{project.Name}': {output}", antName: who);
            return ok
                ? ApiJson.Ok(new Dictionary<string, object?> { ["result"] = output }, "Committed.")
                : ApiJson.Error(output, "bad_request");
        });

        /*
         * v0.3.8.48 — project schedules. Real persistence, real execution (ProjectScheduler),
         * honest wording everywhere: schedules run while the Anthill host runs.
         */
        app.MapGet("/projects/{id}/schedules", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            if (Queen.Memory.LoadProject(id) is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["schedules"] = Queen.Memory.LoadProjectSchedules(id).Select(ScheduleView).ToList(),
                ["note"] = "Schedules execute while the Anthill host is running.",
            });
        });

        app.MapPost("/projects/{id}/schedules", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            if (Queen.Memory.LoadProject(id) is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            ScheduleRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ScheduleRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var error = ValidateSchedule(body);
            if (error is not null) return ApiJson.Error(error, "bad_request");

            var who = CurrentUsername(ctx) ?? "operator";
            var now = AnthillTime.NowUtc();
            var s = new Anthill.Core.Projects.ProjectSchedule
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                ProjectId = id,
                Name = body!.Name!.Trim(),
                Prompt = (body.Prompt ?? "").Trim(),
                TriggerType = body.Trigger!,
                Cron = body.Cron,
                OneTimeAt = AnthillTime.ParseIsoOrNull(body.OneTimeAt),
                LocalTime = body.LocalTime,
                Timezone = string.IsNullOrWhiteSpace(body.Timezone) ? "UTC" : body.Timezone!,
                ApprovalMode = ParsePolicy(body.ApprovalMode),
                Provider = body.Provider, Model = body.Model,
                OverlapPolicy = body.OverlapPolicy == "queue" ? "queue" : "skip",
                CreatedBy = who, UpdatedBy = who,
            };
            s = s with { NextRunAt = s.ComputeNextRun(now) };
            Queen.Memory.SaveSchedule(s);
            return ApiJson.Ok(ScheduleView(s), $"Schedule \"{s.Name}\" created.");
        });

        app.MapPatch("/schedules/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            var s = Queen.Memory.LoadSchedule(id);
            if (s is null) return ApiJson.Error($"No schedule '{id}'.", "not_found");
            ScheduleRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ScheduleRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var who = CurrentUsername(ctx) ?? "operator";
            var updated = s with
            {
                Name = string.IsNullOrWhiteSpace(body?.Name) ? s.Name : body!.Name!.Trim(),
                Prompt = body?.Prompt ?? s.Prompt,
                TriggerType = string.IsNullOrWhiteSpace(body?.Trigger) ? s.TriggerType : body!.Trigger!,
                Cron = body?.Cron ?? s.Cron,
                OneTimeAt = body?.OneTimeAt is null ? s.OneTimeAt : AnthillTime.ParseIsoOrNull(body.OneTimeAt),
                LocalTime = body?.LocalTime ?? s.LocalTime,
                Timezone = string.IsNullOrWhiteSpace(body?.Timezone) ? s.Timezone : body!.Timezone!,
                ApprovalMode = body?.ApprovalMode is null ? s.ApprovalMode : ParsePolicy(body.ApprovalMode),
                Provider = body?.Provider ?? s.Provider,
                Model = body?.Model ?? s.Model,
                Enabled = body?.Enabled ?? s.Enabled,
                OverlapPolicy = body?.OverlapPolicy is null ? s.OverlapPolicy : (body.OverlapPolicy == "queue" ? "queue" : "skip"),
                UpdatedBy = who, UpdatedAt = AnthillTime.NowUtc(),
            };
            if (updated.TriggerType == "cron" && !Anthill.Core.Projects.ProjectSchedule.CronIsValid(updated.Cron))
                return ApiJson.Error("That cron expression is not valid (five fields: minute hour day month weekday; numbers, * and comma lists).", "bad_request");
            updated = updated with { NextRunAt = updated.ComputeNextRun(AnthillTime.NowUtc()) };
            Queen.Memory.SaveSchedule(updated);
            return ApiJson.Ok(ScheduleView(updated), "Schedule updated.");
        });

        app.MapDelete("/schedules/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            if (Queen.Memory.LoadSchedule(id) is null) return ApiJson.Error($"No schedule '{id}'.", "not_found");
            Queen.Memory.DeleteSchedule(id);
            return ApiJson.Ok(null, "Schedule deleted. Its run history is kept.");
        });

        app.MapPost("/schedules/{id}/run", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            var s = Queen.Memory.LoadSchedule(id);
            if (s is null) return ApiJson.Error($"No schedule '{id}'.", "not_found");
            var run = Queen.Scheduler.RunNow(s, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["run_id"] = run.Id, ["status"] = run.Status,
                ["conversation_id"] = run.ConversationId, ["summary"] = run.Summary,
            }, run.Status == "skipped_overlap" ? "Skipped — the previous run is still in progress."
             : run.Status == "waiting_approval" ? "Started — waiting on your approval in its conversation."
             : "Run finished.");
        });

        app.MapGet("/schedules/{id}/runs", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            if (Queen.Memory.LoadSchedule(id) is null) return ApiJson.Error($"No schedule '{id}'.", "not_found");
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["runs"] = Queen.Memory.LoadScheduleRuns(id).Select(r => new Dictionary<string, object?>
                {
                    ["id"] = r.Id, ["status"] = r.Status, ["trigger"] = r.Trigger,
                    ["conversation_id"] = r.ConversationId, ["summary"] = r.Summary,
                    ["started_at"] = r.StartedAt.ToIso(), ["finished_at"] = r.FinishedAt?.ToIso(),
                }).ToList(),
            });
        });

        static Dictionary<string, object?> ScheduleView(Anthill.Core.Projects.ProjectSchedule s) => new()
        {
            ["id"] = s.Id, ["project_id"] = s.ProjectId, ["name"] = s.Name, ["prompt"] = s.Prompt,
            ["trigger"] = s.TriggerType, ["cron"] = s.Cron, ["one_time_at"] = s.OneTimeAt?.ToIso(),
            ["local_time"] = s.LocalTime, ["timezone"] = s.Timezone,
            ["approval_mode"] = s.ApprovalMode.ToString().ToLowerInvariant(),
            ["provider"] = s.Provider, ["model"] = s.Model, ["enabled"] = s.Enabled,
            ["overlap_policy"] = s.OverlapPolicy,
            ["next_run_at"] = s.NextRunAt?.ToIso(), ["last_run_at"] = s.LastRunAt?.ToIso(),
            ["created_by"] = s.CreatedBy, ["updated_by"] = s.UpdatedBy,
        };

        static string? ValidateSchedule(ScheduleRequest? b)
        {
            if (string.IsNullOrWhiteSpace(b?.Name)) return "A schedule needs a name.";
            if (!Anthill.Core.Projects.ProjectSchedule.TriggerTypes.Contains(b!.Trigger ?? ""))
                return "Trigger must be one of: " + string.Join(", ", Anthill.Core.Projects.ProjectSchedule.TriggerTypes);
            if (b.Trigger != "manual" && string.IsNullOrWhiteSpace(b.Prompt))
                return "A schedule that fires on its own needs instructions to run.";
            if (b.Trigger == "once" && AnthillTime.ParseIsoOrNull(b.OneTimeAt) is null)
                return "A one-time schedule needs a valid ISO timestamp.";
            if (b.Trigger == "cron" && !Anthill.Core.Projects.ProjectSchedule.CronIsValid(b.Cron))
                return "That cron expression is not valid (five fields: minute hour day month weekday; numbers, * and comma lists).";
            if (!string.IsNullOrWhiteSpace(b.Timezone))
                try { TimeZoneInfo.FindSystemTimeZoneById(b.Timezone!); }
                catch { return $"Unknown timezone '{b.Timezone}'. Use an IANA id like America/Chicago."; }
            return null;
        }

        static EscalationPolicy ParsePolicy(string? p) =>
            Enum.TryParse<EscalationPolicy>(p, ignoreCase: true, out var pol) ? pol : EscalationPolicy.Ask;

        // The conversations inside one project — what the Projects page opens.
        app.MapGet("/projects/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var project = Queen.Memory.LoadProject(id);
            if (project is null) return ApiJson.Error($"No project '{id}'.", "not_found");
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["id"] = project.Id, ["name"] = project.Name,
                ["description_md"] = project.DescriptionMd, ["path"] = project.Path,
                // v0.3.8.52 (third field round): the operator SETS the working directory — the
                // colony only suggests. The suggestion keeps every project under one shared root
                // with its own tree; the files pane prefills it, and nothing is created until
                // the operator says so.
                ["suggested_path"] = Anthill.Core.Projects.ProjectRoots.DefaultFor(project),
                ["archived"] = project.Archived,
                ["default_policy"] = project.EffectiveDefaultPolicy.ToString().ToLowerInvariant(),
                ["default_policy_by"] = project.DefaultPolicyBy,
                ["default_provider"] = project.DefaultProvider, ["default_model"] = project.DefaultModel,
                ["schedule_count"] = Queen.Memory.LoadProjectSchedules(id).Count,
                ["conversations"] = Queen.Memory.LoadProjectConversations(id).Select(c => new Dictionary<string, object?>
                {
                    ["id"] = c.Id, ["title"] = c.Title, ["pinned"] = c.Pinned,
                    ["cancelled"] = c.Cancelled, ["mission_ids"] = c.MissionIds,
                    ["updated_at"] = c.UpdatedAt.ToIso(),
                }).ToList(),
            });
        });

        // v0.3.8.47: import — the inverse of export, for bringing a transcript in from another
        // ANTHILL (or anywhere that can produce the JSON shape). The imported turns are recorded
        // as HISTORY: no provider or model is invented for them, and the conversation gets its
        // own project like any other. Nothing about an import pretends this colony did the work.
        app.MapPost("/conversations/import", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            ImportRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ImportRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var turns = body?.Turns ?? new List<ImportTurn>();
            if (turns.Count == 0) return ApiJson.Error("Nothing to import — no turns.", "bad_request");
            if (turns.Count > 2000) return ApiJson.Error("Too many turns to import (max 2000).", "bad_request");

            var title = string.IsNullOrWhiteSpace(body?.Title) ? "Imported conversation" : body!.Title!.Trim();
            var project = new Anthill.Core.Projects.Project
                { Id = Guid.NewGuid().ToString("N")[..12], Name = title };
            Queen.Memory.SaveProject(project);
            var conversation = new Conversation
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Title = title, ProjectId = project.Id,
            };
            Queen.Memory.SaveConversation(conversation);
            var ordinal = 0;
            foreach (var t in turns)
            {
                var role = string.Equals(t.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                Queen.Memory.SaveConversationTurn(new ConversationTurn(
                    Guid.NewGuid().ToString("N")[..12], conversation.Id, ++ordinal, role, t.Content ?? ""));
            }
            return ApiJson.Ok(new Dictionary<string, object?>
                { ["id"] = conversation.Id, ["turns"] = ordinal },
                $"Imported {ordinal} turn(s) into \"{title}\".");
        });

        // v0.3.8.46: pin / unpin. Two explicit endpoints rather than a toggle, so a stale rail
        // can never invert the operator's intent by firing the toggle against old state.
        app.MapPost("/conversations/{id}/pin", (HttpContext ctx, string id) => SetPinned(ctx, id, true));
        app.MapPost("/conversations/{id}/unpin", (HttpContext ctx, string id) => SetPinned(ctx, id, false));

        IResult SetPinned(HttpContext ctx, string id, bool pinned)
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            var conversation = Queen.Memory.LoadConversation(id);
            if (conversation is null) return ApiJson.Error($"No conversation '{id}'.", "not_found");

            // UpdatedAt is deliberately untouched: pinning is shelving, not activity.
            Queen.Memory.SaveConversation(conversation with { Pinned = pinned });
            return ApiJson.Ok(new Dictionary<string, object?> { ["pinned"] = pinned },
                pinned ? "Pinned." : "Unpinned.");
        }

        // v0.3.8.46: the transcript as a file the operator can keep or hand to someone — markdown,
        // rendered from the same rows the detail endpoint serves. Decisions included: an exported
        // audit missing its permissions record is half an audit.
        app.MapGet("/conversations/{id}/export", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var conversation = Queen.Memory.LoadConversation(id);
            if (conversation is null) return ApiJson.Error($"No conversation '{id}'.", "not_found");

            var turns = Queen.Memory.LoadConversationTurns(id);
            var decisions = Queen.Memory.LoadEscalationDecisions(id);

            var md = new System.Text.StringBuilder();
            md.AppendLine($"# {(string.IsNullOrWhiteSpace(conversation.Title) ? "Conversation " + conversation.Id : conversation.Title)}");
            md.AppendLine();
            md.AppendLine($"- Exported: {AnthillTime.NowUtc().ToIso()}");
            md.AppendLine($"- Conversation: `{conversation.Id}` (role: {conversation.Role})");
            if (conversation.MissionIds.Count > 0)
                md.AppendLine($"- Missions: {string.Join(", ", conversation.MissionIds.Select(m => $"`{m}`"))}");
            md.AppendLine();
            foreach (var t in turns)
            {
                var who = t.Role == "user" ? "Operator"
                    : string.IsNullOrWhiteSpace(t.Provider) ? "Colony" : $"Colony ({t.Provider}{(string.IsNullOrWhiteSpace(t.Model) ? "" : " · " + t.Model)})";
                md.AppendLine($"## {who} — {t.CreatedAt.ToIso()}");
                md.AppendLine();
                md.AppendLine(t.Content);
                if (!string.IsNullOrWhiteSpace(t.MissionId))
                    md.AppendLine($"\n> Escalated to mission `{t.MissionId}`.");
                md.AppendLine();
            }
            if (decisions.Count > 0)
            {
                md.AppendLine("## Escalation decisions");
                md.AppendLine();
                foreach (var d in decisions)
                    md.AppendLine($"- {d.DecidedAt.ToIso()} — {(d.Allowed ? "ALLOWED" : "REFUSED")}: {d.Action} " +
                                  $"(policy {d.Policy}, by {d.DecidedBy}{(string.IsNullOrWhiteSpace(d.Reason) ? "" : ", " + d.Reason)})");
            }

            var fname = "conversation-" + conversation.Id + ".md";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fname}\"";
            return Results.Text(md.ToString(), "text/markdown", System.Text.Encoding.UTF8);
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
                // v0.3.8.42: the LIST projected this and the DETAIL did not, so the chat page had
                // to guess from the prose — and Doing() answers "cancelled" as a STRING, which a
                // truthiness check renders as "Working…", keeps a live Stop over a stopped
                // conversation, and overwrites refusal summaries. State travels as state.
                ["cancelled"] = state.Cancelled,
                ["policy"] = state.Policy.ToString().ToLowerInvariant(),
                // v0.3.8.51, found live: the DETAIL never carried project_id — only the list did —
                // so the files pane and the gates affordance dead-ended on a field that did not
                // exist and told the operator to "open a conversation" they were sitting in.
                ["project_id"] = conversation.ProjectId,
                // v0.3.8.52 (field report: with the files pane open "you have no way to see what
                // the project is") — the NAME rides the detail so the chat header can wear it.
                ["project_name"] = conversation.ProjectId is { } cpid
                    ? Queen.Memory.LoadProject(cpid)?.Name : null,
                ["mission_ids"] = conversation.MissionIds,
                ["turns"] = Queen.Memory.LoadConversationTurns(id).Select(t => new Dictionary<string, object?>
                {
                    ["ordinal"] = t.Ordinal, ["role"] = t.Role, ["content"] = t.Content,
                    ["provider"] = t.Provider, ["model"] = t.Model,
                    ["tools_offered"] = t.ToolsOffered, ["tools_called"] = t.ToolsCalled,
                    ["mission_id"] = t.MissionId, ["created_at"] = t.CreatedAt.ToIso(),
                    // v0.3.8.46: null when the provider did not report — the UI shows nothing
                    // rather than a fabricated zero.
                    ["prompt_tokens"] = t.PromptTokens, ["completion_tokens"] = t.CompletionTokens,
                    // v0.3.8.47: names and sizes only — the content is in the record for export
                    // and prompting; the transcript shows what was attached, not a wall of text.
                    ["attachments"] = Queen.Memory.LoadTurnAttachments(t.Id)
                        .Select(a => new Dictionary<string, object?> { ["filename"] = a.Filename, ["bytes"] = a.Bytes })
                        .ToList(),
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
                // v0.3.8.55 (field report): a checkout named e958531b-… tells the operator
                // nothing. The mission's own goal is the human name — joined here, bounded, and
                // absent (not invented) when the mission is gone.
                ["mission_goal"] = TextUtil.Truncate(
                    Queen.Memory.GetMission(w.MissionId)?.GetValueOrDefault("goal")?.ToString() ?? "", 90, "…"),
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
                //
                // v0.3.8.41 — CONFINED, like the routed one. Building directly is a deliberate
                // shortcut around the router's timeout, not around its safety: this endpoint starts
                // a real agent process, and an agent with Writes = true given no working directory
                // acts in whatever directory the API host was started from. Skipping the router must
                // not mean skipping the boundary — that is how the routed path lost it in the first
                // place, by omitting one argument at the only site that supplied it.
                IReasoningProvider probe = new AgentCliProvider(
                    agent, TimeSpan.FromSeconds(30), ReasoningProviders.AgentWorkspaceRoot);
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
