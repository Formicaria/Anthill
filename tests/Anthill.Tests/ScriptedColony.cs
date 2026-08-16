using System.Text.Json;
using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Anthill.Core.Models;
using Anthill.SDK.Reasoning;
using Anthill.SDK.Tools;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.53 — THE SCRIPTED REASONING PROVIDER: AUTONOMY-10 Phase 2's keystone, the substitution
/// the backend audit explicitly sanctions ("scripted reasoning/model provider" at its documented
/// adapter boundary). Everything deterministic downstream — the composed code-patch lifecycle,
/// the medic repair with fresh evidence, all-twelve-through-real-triggers — depends on a model
/// whose answers are known in advance, delivered through the REAL plumbing.
///
/// What is real here, deliberately: registration goes through
/// <see cref="ReasoningProviders.Register"/> — the exact call a module's composition root makes —
/// so the router resolves this factory the way it resolves Ollama's; the route table is the real
/// <see cref="AnthillRuntime.ModelRouting"/>; capability discovery goes through the real probe
/// interface. The ONLY fake thing is the answer.
///
/// What keeps it inert everywhere else: the factory serves only provider id "scripted", which no
/// production route ever names; the probe answers null for every other provider, which
/// <c>CapabilitiesFor</c> reads as "fall back to the name table" — bit-identical to the behaviour
/// with no probe registered. There is no Reset() in ReasoningProviders (removed on purpose), and
/// none is needed: an unrouted factory is unreachable.
///
/// Role dispatch: every ant and the planner stamp their prompts with the header convention
/// <c>| role: name |</c> (Ants.cs, Planner.cs — the producers, not an assumption), so the
/// provider reads the role out of the request it was handed and answers from that role's script.
/// </summary>
public static class ScriptedColony
{
    public const string ProviderId = "scripted";
    public const string ModelId = "scripted-v1";

    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>The scenario's current script book. Set by <see cref="Begin"/>, read per call.</summary>
    internal static ScriptBook? Current;

    /// <summary>
    /// Register the factory and probe through the production registration path, once per test
    /// process. Idempotent; safe under parallel collections because the provider is unreachable
    /// until a test routes a role to it.
    /// </summary>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) return;
            ReasoningProviders.Register(new ScriptedProviderFactory());
            ReasoningProviders.RegisterProbe(new ScriptedCapabilityProbe());
            _registered = true;
        }
    }

    /// <summary>
    /// Route every named role to the scripted provider and install <paramref name="book"/> as the
    /// live script. Disposing restores the previous routes and clears the book. Callers hold the
    /// "specialist-gates" collection, the same serialization every static-state test uses.
    /// </summary>
    public static IDisposable Begin(ScriptBook book, params string[] roles)
    {
        EnsureRegistered();
        var table = AnthillRuntime.ModelRouting;
        var saved = new Dictionary<string, Dictionary<string, string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles)
        {
            saved[role] = table.TryGetValue(role, out var prior) ? prior : null;
            table[role] = new Dictionary<string, string>
            {
                ["provider"] = ProviderId, ["model"] = ModelId,
            };
        }
        Current = book;
        return new Restore(saved);
    }

    private sealed class Restore(Dictionary<string, Dictionary<string, string>?> saved) : IDisposable
    {
        public void Dispose()
        {
            var table = AnthillRuntime.ModelRouting;
            foreach (var (role, prior) in saved)
            {
                if (prior is null) table.Remove(role);
                else table[role] = prior;
            }
            Current = null;
        }
    }

    /// <summary>
    /// Reads the role out of the SYSTEM message's header — where the producers now put it.
    ///
    /// v0.3.8.67 — this doc used to say "the prompt's own header", and that stopped being true at
    /// v0.3.8.60 when the role contract moved to the system channel. `RoleSystemPrompt` emits
    /// `ANTHILL v… | role: builder | …`; the user turn carries the task and no longer carries a
    /// banner. The harness kept working only because the loop ran BACKWARDS and fell through to the
    /// system message — right answer, wrong reason, and a comment describing a convention that had
    /// moved.
    ///
    /// Reading the system message FIRST is not tidying. The user turn now contains the mission goal
    /// and, via `ComposeMissionGoal`, a bounded slice of the prior transcript — which can quote an
    /// earlier answer that itself contained a role header. Under the old backwards scan that quoted
    /// header would win, and the scripted provider would answer as the wrong role: a scenario would
    /// fail, or worse pass, for a reason nobody would look for in a test fixture.
    ///
    /// The user turn is still searched as a fallback, because a caller that builds a single-message
    /// request (`ModelRequest.FromPrompt`) is legitimate and must not silently get role "".
    /// </summary>
    internal static string RoleOf(ModelRequest request)
    {
        static string? Header(string? content)
        {
            var m = Regex.Match(content ?? "", @"\|\s*role:\s*([A-Za-z_-]+)\s*\|");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        foreach (var m in request.Messages)
            if (string.Equals(m.Role, ModelMessage.System, StringComparison.OrdinalIgnoreCase)
                && Header(m.Content) is { } fromContract)
                return fromContract;

        // No system message: a single-prompt caller, whose banner (if any) is in the user turn.
        for (var i = request.Messages.Count - 1; i >= 0; i--)
            if (Header(request.Messages[i].Content) is { } fromPrompt) return fromPrompt;

        return "";
    }
}

/// <summary>
/// The answers, per role, consumed in order — a role asked more times than it was scripted for
/// replays its LAST answer (deterministic, and a repair loop legitimately re-asks the coder).
/// Every request is recorded so a scenario can assert on what each role was actually asked.
/// </summary>
public sealed class ScriptBook
{
    private readonly Dictionary<string, Queue<string>> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _last = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Role, string Prompt)> Requests { get; } = new();

    /// <summary>Answer(s) for a role, delivered first-to-last, then the last one repeats.</summary>
    public ScriptBook Role(string role, params string[] answers)
    {
        if (!_scripts.TryGetValue(role, out var q)) _scripts[role] = q = new Queue<string>();
        foreach (var a in answers) q.Enqueue(a);
        if (answers.Length > 0) _last[role] = answers[^1];
        return this;
    }

    internal string? Answer(string role, string prompt)
    {
        lock (Requests) Requests.Add((role, prompt));
        if (_scripts.TryGetValue(role, out var q) && q.Count > 0)
        {
            var next = q.Dequeue();
            _last[role] = q.Count > 0 ? _last[role] : next;
            return next;
        }
        return _last.TryGetValue(role, out var last) ? last : null;
    }
}

/// <summary>
/// A scripted <c>web_search</c>, and the same argument as <see cref="ScriptedProvider"/> one adapter
/// over. v0.3.8.68.
///
/// WHY THIS EXISTS. The web ant was the second of the two roles v0.3.8.68 found decorative in the
/// all-twelve mission. Its only real trigger is a search, a search means the network, and a suite
/// that reaches the network is neither deterministic nor honest about what it proved — so the ant
/// was given a task it could not do, refused it with a typed block, and the refusal was counted as
/// the role having run.
///
/// The reasoning provider had this problem first and the repository already answered it: substitute
/// at the OUTERMOST adapter and leave everything behind it real. That is what this is. What runs for
/// real, once the results are handed back: URL decoding, SSRF refusal, dedupe by normalised URL,
/// domain quality scoring, the source-confidence threshold, `SaveSourceRecord`, and both pheromone
/// trails. Only the socket is missing.
///
/// FAIL-SAFE BY CONSTRUCTION, which matters more here than in the model case. Scenarios shadow the
/// module's real tool by registering this one under the same name AFTER the module's tools are
/// adopted (<c>ToolRegistry.Register</c> is last-write-wins). If that shadowing ever stopped working
/// — a registration order change, a rename — the tool underneath is the real one, whose gate the
/// scenarios deliberately leave OFF. So the failure mode of this fixture breaking is a deterministic
/// refusal, never a silent outbound request from a unit test.
/// </summary>
public sealed class ScriptedWebSearchTool : ITool
{
    public string Name => "web_search";
    public string Description => "Scripted web search — fixed results, no network.";

    private readonly string _payload;

    /// <summary>Every query it is asked is recorded, so a scenario can assert the ant asked
    /// something derived from the real goal rather than that it merely ran.</summary>
    public List<string> Queries { get; } = new();

    /// <summary>
    /// Results in the shape <c>WebResearchAnt</c> parses: <c>{"results":[{title,url,snippet}]}</c>.
    /// Public https URLs on distinct hosts, because the ant's real dedupe and SSRF checks run on
    /// them and a scenario that fed loopback or duplicate URLs would be testing the fixture.
    /// </summary>
    public ScriptedWebSearchTool(params (string Title, string Url, string Snippet)[] results) =>
        _payload = JsonSerializer.Serialize(new
        {
            results = results.Select(r => new { title = r.Title, url = r.Url, snippet = r.Snippet }),
        });

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        lock (Queries) Queries.Add(args.GetValueOrDefault("query")?.ToString() ?? "");
        return new ToolResult(Name, success: true, output: _payload);
    }
}

/// <summary>Serves exactly one provider id; unreachable unless a route names it.</summary>
internal sealed class ScriptedProviderFactory : IReasoningProviderFactory
{
    public bool CanServe(string providerId) =>
        string.Equals(providerId, ScriptedColony.ProviderId, StringComparison.OrdinalIgnoreCase);

    public IReasoningProvider Create(ReasoningProviderContext context) => new ScriptedProvider();
}

internal sealed class ScriptedProvider : IReasoningProvider
{
    public ModelResponse Send(ModelRequest request, int retries = 2)
    {
        var book = ScriptedColony.Current;
        var role = ScriptedColony.RoleOf(request);
        var prompt = request.Messages.Count > 0 ? request.Messages[^1].Content ?? "" : "";
        var answer = book?.Answer(role, prompt);
        return answer is null
            // No script for this role is a SCENARIO defect and must fail loudly as a provider
            // error, never as an invented answer the scenario silently absorbs.
            ? new ModelResponse
            {
                Status = ModelCallOutcome.Empty,
                Content = $"scripted provider has no script for role '{role}'",
            }
            : new ModelResponse { Status = ModelCallOutcome.Ok, Content = answer };
    }
}

/// <summary>Full capabilities for the scripted provider; null/empty for everything else, which
/// the router reads as "fall back to the name table" — bit-identical to having no probe at all.</summary>
internal sealed class ScriptedCapabilityProbe : IModelCapabilityProbe
{
    private static readonly ModelCapabilities Full = new()
    {
        ToolCalling = true, StructuredOutput = true, Streaming = false,
        Vision = false, Embeddings = false, Reasoning = true,
        ContextWindowTokens = 1_000_000,
    };

    public ModelCapabilities? For(string providerId, string model) =>
        string.Equals(providerId, ScriptedColony.ProviderId, StringComparison.OrdinalIgnoreCase)
            ? Full : null;

    public IReadOnlyDictionary<string, ModelCapabilities> Snapshot(string providerId) =>
        string.Equals(providerId, ScriptedColony.ProviderId, StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, ModelCapabilities> { [ScriptedColony.ModelId] = Full }
            : new Dictionary<string, ModelCapabilities>();

    // Nothing to fetch: the script IS the cache. Present because v3.8.2's ordering defect makes
    // Warm part of the contract, and a probe that raced it would repeat that release's bug.
    public void Warm() { }
}
