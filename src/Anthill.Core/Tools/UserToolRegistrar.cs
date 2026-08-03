using Anthill.Core.Configuration;

namespace Anthill.Core.Tools;

/// <summary>
/// v3.4.1 (ADR-006) — what <see cref="ToolAuthorization"/> reads to decide whether a role may
/// dispatch an operator-defined tool.
///
/// A projection of the registered definitions, not a second source of truth: the ONLY writer is
/// <see cref="UserToolRegistrar"/>, at registration, and the map is replaced wholesale rather than
/// merged. Merging is how a revoked tool keeps working forever — the same mistake the Ollama
/// capability cache made and fixed for the same reason.
///
/// Static because <see cref="ToolAuthorization"/> is static, and the alternative — threading a
/// dependency through every call site of the enforcement chokepoint — would be a large refactor in
/// service of a table with one writer. <see cref="Clear"/> exists so tests are not order-dependent.
/// </summary>
public static class UserToolGrants
{
    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, ToolDefinition> _grants =
        new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string toolName, out ToolDefinition definition)
    {
        var snapshot = _grants;
        return snapshot.TryGetValue(toolName ?? "", out definition!);
    }

    public static IReadOnlyCollection<ToolDefinition> All => _grants.Values.ToList();

    /// <summary>Replace the whole table. Called by the registrar; nothing else may write here.</summary>
    internal static void Replace(IEnumerable<ToolDefinition> definitions)
    {
        lock (Gate)
        {
            _grants = definitions.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Clear() => Replace(Array.Empty<ToolDefinition>());
}

/// <summary>The outcome of trying to register one definition.</summary>
public sealed record ToolRegistration(string Name, bool Registered, IReadOnlyList<string> Problems)
{
    public static ToolRegistration Ok(string name) => new(name, true, Array.Empty<string>());
    public static ToolRegistration Rejected(string name, IReadOnlyList<string> problems) =>
        new(name, false, problems);
}

/// <summary>
/// v3.4.1 (ADR-006) — turns stored <see cref="ToolDefinition"/>s into registered
/// <see cref="ITool"/>s, and is the single place that decides whether one is fit to register.
///
/// The exit gate this exists to satisfy: a user-registered tool is subject to the SAME authorization
/// and projection rules as a built-in. It achieves that by not being special anywhere — a validated
/// definition becomes a <see cref="UserDefinedTool"/> in the ordinary <see cref="ToolRegistry"/>,
/// so <see cref="ToolSchemaProjection"/> offers it, <see cref="ToolRegistry.RunTool"/> dispatches it
/// under <see cref="ToolAuthorization"/>, its failures are classified, and <c>GET /tools</c> reports
/// it. No path in the harness asks whether a tool was compiled in or declared.
///
/// One definition failing validation must never stop the others from loading. A colony that refuses
/// to start because one stored tool has a bad URL is a colony an operator cannot fix without
/// hand-editing the database.
/// </summary>
public sealed class UserToolRegistrar
{
    private readonly IReadOnlyDictionary<ToolKind, IToolKindExecutor> _executors;

    public UserToolRegistrar(params IToolKindExecutor[] executors) =>
        _executors = executors.ToDictionary(e => e.Kind);

    /// <summary>The kinds this build can construct. Matches <see cref="ToolKinds.Buildable"/>.</summary>
    public static UserToolRegistrar Default() => new(new HttpToolKind());

    /// <summary>
    /// Everything a definition must satisfy before it becomes callable: its own shape, and the
    /// config its kind needs. Both are reported together — an operator fixing a definition wants
    /// every problem at once, not one per round trip.
    /// </summary>
    public IReadOnlyList<string> Validate(ToolDefinition definition)
    {
        var problems = definition.Validate().ToList();

        // Kind config is only checkable when the kind is buildable; reporting "url is required" for
        // an MCP tool would be noise on top of the real answer, which is that MCP is not built yet.
        if (problems.Count == 0 && _executors.TryGetValue(definition.Kind, out var executor))
            problems.AddRange(executor.ValidateConfig(definition));

        return problems;
    }

    /// <summary>
    /// Register every enabled definition into <paramref name="registry"/> and publish the grant
    /// table. Returns one outcome per definition, including the rejected ones, because "my tool is
    /// not there" is the question this method exists to answer.
    /// </summary>
    public IReadOnlyList<ToolRegistration> RegisterAll(
        ToolRegistry registry, IEnumerable<ToolDefinition> definitions)
    {
        var results = new List<ToolRegistration>();
        var granted = new List<ToolDefinition>();

        // The feature gate is checked ONCE, here, rather than per definition: with user tools off,
        // nothing is registered and nothing is granted, so a stored definition is inert rather than
        // registered-but-refusing. An inert tool is never offered to a model, which is the correct
        // behaviour — offering a tool that always denies wastes a turn to teach the model nothing.
        if (!AnthillRuntime.EnableUserTools)
        {
            UserToolGrants.Clear();
            return definitions
                .Select(d => ToolRegistration.Rejected(d.Name, new[] { "user tools are disabled by config" }))
                .ToList();
        }

        foreach (var definition in definitions)
        {
            if (!definition.Enabled)
            {
                results.Add(ToolRegistration.Rejected(definition.Name, new[] { "definition is disabled" }));
                continue;
            }

            var problems = Validate(definition);
            if (problems.Count > 0)
            {
                results.Add(ToolRegistration.Rejected(definition.Name, problems));
                continue;
            }

            // Belt and braces: Validate() already rejects a kind outside ToolKinds.Buildable, but a
            // registrar constructed with a narrower executor set would otherwise throw here. A
            // missing executor is a rejection, never an exception thrown through startup.
            if (!_executors.TryGetValue(definition.Kind, out var executor))
            {
                results.Add(ToolRegistration.Rejected(definition.Name,
                    new[] { $"no executor is registered for kind '{definition.Kind}'" }));
                continue;
            }

            registry.Register(new UserDefinedTool(definition, executor));
            granted.Add(definition);
            results.Add(ToolRegistration.Ok(definition.Name));
        }

        // Published only after every definition is processed, so the table never reflects a
        // half-applied load. Wholesale replacement means a definition removed since the last call
        // stops being granted rather than lingering.
        UserToolGrants.Replace(granted);
        return results;
    }
}
