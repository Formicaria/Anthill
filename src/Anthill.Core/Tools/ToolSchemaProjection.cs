using Anthill.Core.Models;

namespace Anthill.Core.Tools;

/// <summary>
/// v3.4.0 (ADR-006) — the registered tools, projected into what a model can be offered.
///
/// This is the join the harness needed: <see cref="ITool"/> already knows its name, description and
/// (now) its argument schema, and <see cref="ModelToolSpec"/> is what the provider wire format
/// consumes. Nothing here invents a description or a schema; it moves the ones the tools already
/// declare.
///
/// THE LOAD-BEARING RULE: a role is offered only the tools it is actually ALLOWED to run.
///
/// It would be easier to offer everything and let <see cref="ToolAuthorization"/> refuse at
/// execution, and it would be wrong in three separate ways. It wastes a model turn on a call that
/// was never going to be permitted. It teaches the model, mid-conversation, that its tools fail at
/// random — and models respond to that by retrying or inventing workarounds. And it leaks the
/// existence and shape of privileged tools to a role that must not have them, which is a
/// capability-disclosure bug even when the call is correctly denied.
///
/// Authorization stays enforced at execution as well; this does not replace it. Offering the right
/// set is a usability and disclosure property, not a security boundary — the boundary is still the
/// check inside <see cref="ToolRegistry.RunTool"/>, and both must hold.
/// </summary>
public static class ToolSchemaProjection
{
    /// <summary>
    /// The tools <paramref name="antName"/> may be offered, in provider-neutral form.
    ///
    /// Deterministically ordered by name: a tool list that reshuffles between calls changes the
    /// prompt for no reason, which defeats provider-side prompt caching and makes two otherwise
    /// identical runs impossible to compare.
    /// </summary>
    public static IReadOnlyList<ModelToolSpec> For(ToolRegistry registry, string? antName)
    {
        if (registry is null) return Array.Empty<ModelToolSpec>();

        var specs = new List<ModelToolSpec>();
        foreach (var tool in registry.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (!ToolAuthorization.Evaluate(antName, tool.Name).Allowed) continue;
            specs.Add(ToSpec(tool));
        }
        return specs;
    }

    /// <summary>
    /// One tool as a model-facing spec. The schema is validated as JSON here rather than trusted:
    /// a malformed schema reaching the provider is rejected for the WHOLE request, so one bad tool
    /// would disable every other tool in the same call. Falling back to the empty-object schema
    /// keeps the rest of the toolset usable and degrades only the offender.
    /// </summary>
    public static ModelToolSpec ToSpec(ITool tool)
    {
        var schema = tool.ParametersJson;
        if (!IsJsonObject(schema)) schema = """{"type":"object","properties":{}}""";
        return new ModelToolSpec(tool.Name, tool.Description ?? "", schema);
    }

    private static bool IsJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
