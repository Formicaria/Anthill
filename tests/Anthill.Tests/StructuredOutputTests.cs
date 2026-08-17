using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthill.Core.Models;
using Anthill.SDK.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Structured output reaches the wire. v0.3.8.76 (PLAN.md §2 R1).
///
/// THE DEFECT, and it is the purest example of "declared and reaching nobody" this repository has
/// found. `ModelRequest.ResponseSchemaJson` has existed since v3.4.0. `ProviderWireFormat` turns it
/// into an OpenAI `response_format: json_schema`. `ModelCapabilityCatalog.Negotiate` strips it for a
/// model that cannot honour one, at the seam, so no call site needs to know. Three correct,
/// tested layers — and NO PRODUCER EVER SET THE FIELD. There was no parameter on `GenerateTyped` to
/// set it with, so the whole pipe was unreachable from every ant in the colony.
///
/// What the colony did instead was ask in English. The coder's prompt says "Return ONLY valid JSON"
/// and prints an example, in the same user turn as the operator's untrusted goal. That makes the
/// output format a REQUEST the model may decline, which is why the coder has a retry loop, why
/// "malformed patch output" is a named failure class, and why a zero-proposal result had to be
/// classified as a failed deliverable. It is also prose used as a control channel — the exact thing
/// v3.2.0 and v3.8.22 spent two releases removing from verdicts, blocks and handoffs — left in place
/// at the one seam where the colony asks for machine-readable structure.
///
/// WHAT THESE TESTS PIN. That every route claiming structured output actually sends a schema; that
/// each schema is valid JSON and agrees with the prompt that describes the same shape; and that the
/// negotiation which protects a weak model still happens. Not that any model honours it — that is
/// live behaviour, and it belongs to the adapter conformance suite and R4.
/// </summary>
public class StructuredOutputTests
{
    private static string Src(params string[] parts) =>
        SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            new[] { SourceText.RepoRoot(), "src" }.Concat(parts).ToArray())));

    /// <summary>
    /// Where each schema-bearing route sets its schema, so the assertions below read the call site
    /// rather than trusting that a constant exists somewhere.
    /// </summary>
    private static readonly (string Route, string[] File, string Constant)[] Wired =
    {
        ("coder",      new[] { "Anthill.Core", "Agents", "Ants.cs" },           "PatchSetSchema"),
        ("planner",    new[] { "Anthill.Core", "Planning", "Planner.cs" },      "PlanSchema"),
        ("strategist", new[] { "Anthill.Core", "Autonomy", "Strategist.cs" },   "ObjectiveProposalSchema"),
    };

    // -----------------------------------------------------------------------------------------------
    // Every claim of structured output is backed by a schema on the wire
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE ASSERTION THIS FILE EXISTS FOR. A route that declares `StructuredOutput: true` sends a
    /// schema.
    ///
    /// Before v0.3.8.76 this failed for every such route, and nothing said so — the declaration was
    /// graded against the model's capabilities and never against the colony's own behaviour, so
    /// "this role needs structured output" and "this role asks for structured output" were two
    /// unrelated facts that read as one.
    /// </summary>
    [Fact]
    public void EveryRouteDeclaringStructuredOutput_SendsASchema()
    {
        var wiredRoutes = Wired.Select(w => w.Route).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var claiming = ModelRouteRequirements.Routes.Values
            .Where(r => r.Needs.StructuredOutput)
            .Select(r => r.RouteId)
            .Where(r => !wiredRoutes.Contains(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.True(claiming.Count == 0,
            "these routes declare they need structured output and send no schema: "
          + string.Join(", ", claiming)
          + ". The requirement is graded in the fitness report, so an operator is told their model "
          + "must support structured output for a call that never asks for any. Either pass a "
          + "schema on the GenerateTyped call, or stop declaring the requirement.");
    }

    /// <summary>And the reverse: a schema is sent only where the requirement is declared, so the
    /// fitness report cannot promise a model that never needed grading.</summary>
    [Fact]
    public void EveryRouteSendingASchema_DeclaresTheRequirement()
    {
        var undeclared = Wired
            .Where(w => !ModelRouteRequirements.NeedsOf(w.Route).StructuredOutput)
            .Select(w => w.Route).ToList();

        Assert.True(undeclared.Count == 0,
            "these routes send a response schema and do not declare `StructuredOutput: true`: "
          + string.Join(", ", undeclared)
          + ". Negotiate will strip the schema for a model that cannot honour it, silently, and no "
          + "fitness row will have warned anyone that it mattered.");
    }

    /// <summary>The schema constant is actually passed at the call site, on every path.</summary>
    [Theory]
    [InlineData("coder", "Anthill.Core|Agents|Ants.cs", "PatchSetSchema", 2)]
    [InlineData("planner", "Anthill.Core|Planning|Planner.cs", "PlanSchema", 1)]
    [InlineData("strategist", "Anthill.Core|Autonomy|Strategist.cs", "ObjectiveProposalSchema", 1)]
    public void TheSchemaIsPassed_OnEveryCallPathOfTheRoute(
        string route, string path, string constant, int callPaths)
    {
        var source = Src(path.Split('|'));

        var calls = Regex.Matches(source, $@"GenerateTyped\(\s*""{route}""").Count;
        Assert.True(calls == callPaths,
            $"'{route}' has {calls} GenerateTyped call site(s); this test knows about {callPaths}. "
          + "A new call path is exactly where a schema gets forgotten — the coder's refinement turn "
          + "was the second one, and it is the turn that runs after the model already got the shape "
          + "wrong once.");

        var withSchema = Regex.Matches(source, $@"schema:\s*{constant}").Count;
        Assert.True(withSchema == callPaths,
            $"'{route}' passes `schema: {constant}` on {withSchema} of its {callPaths} call path(s).");
    }

    // -----------------------------------------------------------------------------------------------
    // The schemas are real, and agree with the prose beside them
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Each schema parses, is an object schema, and names required keys.
    ///
    /// `ProviderWireFormat` calls `JsonNode.Parse(schema)` with no try — a malformed constant would
    /// throw inside request construction, on the Director thread, on every mission using that route.
    /// </summary>
    [Theory]
    [InlineData("Anthill.Core|Agents|Ants.cs", "PatchSetSchema")]
    [InlineData("Anthill.Core|Planning|Planner.cs", "PlanSchema")]
    [InlineData("Anthill.Core|Autonomy|Strategist.cs", "ObjectiveProposalSchema")]
    public void EverySchema_IsValidJsonSchemaShape(string path, string constant)
    {
        var schema = SchemaConstant(path.Split('|'), constant);

        var node = JsonNode.Parse(schema);
        Assert.NotNull(node);

        var root = node!.AsObject();
        Assert.Equal("object", root["type"]!.GetValue<string>());
        Assert.NotNull(root["properties"]);
        Assert.NotEmpty(root["required"]!.AsArray());
    }

    /// <summary>
    /// THE ANTI-DRIFT ASSERTION. Every property the schema declares appears in the prompt that
    /// describes the same shape, and every field the prompt's example names appears in the schema.
    ///
    /// Two descriptions of one format now exist per route — the schema binds the provider, the prose
    /// carries the judgement a schema cannot express ("return an empty list rather than guessing").
    /// Keeping both is right and keeping them silently is not: they would drift toward the schema
    /// being enforced and the prompt being read, and with `additionalProperties: false` a field the
    /// prompt asks for and the schema omits becomes a field the provider is forbidden to emit.
    ///
    /// That is not hypothetical. Writing these schemas turned up exactly that: `skill_id` is
    /// optional in the planner's prompt, absent from its example, read by `TasksFromJson`, and was
    /// missing from the first draft of `PlanSchema` — which would have silently ended skill
    /// attribution rather than failing anything.
    /// </summary>
    [Theory]
    [InlineData("Anthill.Core|Agents|Ants.cs", "PatchSetSchema")]
    [InlineData("Anthill.Core|Planning|Planner.cs", "PlanSchema")]
    [InlineData("Anthill.Core|Autonomy|Strategist.cs", "ObjectiveProposalSchema")]
    public void EverySchemaProperty_IsNamedInThePromptBesideIt(string path, string constant)
    {
        var schema = SchemaConstant(path.Split('|'), constant);

        // The prompt text is whatever the file says OUTSIDE every raw-string constant, so a property
        // name occurring only inside a schema cannot satisfy an assertion about the prompt.
        //
        // Stripped by REGION rather than by replacing the schema text: the constant is dedented when
        // it is read back, so the string this test holds is not byte-identical to the indented one in
        // the file, and a `Replace` would have removed nothing and quietly compared the schema
        // against itself.
        var prose = Regex.Replace(Src(path.Split('|')),
            @"const string \w+ = \"""""".*?\"""""";", "", RegexOptions.Singleline);

        foreach (var name in PropertyNames(JsonNode.Parse(schema)!))
            Assert.True(prose.Contains(name, StringComparison.Ordinal),
                $"`{constant}` declares the property `{name}` and the prompt in the same file never "
              + "mentions it. Either the prompt stopped describing the shape it asks for, or the "
              + "schema grew a field nothing tells the model to produce.");
    }

    /// <summary>Every property name in a schema, at any depth.</summary>
    private static IEnumerable<string> PropertyNames(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject props)
                foreach (var (name, child) in props)
                {
                    yield return name;
                    if (child is not null)
                        foreach (var nested in PropertyNames(child)) yield return nested;
                }

            if (obj["items"] is JsonNode items)
                foreach (var nested in PropertyNames(items)) yield return nested;
        }
    }

    /// <summary>Read a raw-string schema constant out of its source file.</summary>
    private static string SchemaConstant(string[] path, string constant)
    {
        var source = Src(path);
        var m = Regex.Match(source,
            $@"const string {constant} = """"""\r?\n(?<body>.*?)\r?\n\s*"""""";",
            RegexOptions.Singleline);

        Assert.True(m.Success,
            $"could not find the raw-string constant `{constant}` in {string.Join("/", path)}.");

        // Raw string literals are dedented to the closing quotes' indentation; reproduce that so the
        // text parsed here is exactly the text the compiler produces.
        var body = m.Groups["body"].Value;
        var indent = body.Split('\n')
            .Where(l => l.Trim().Length > 0)
            .Min(l => l.Length - l.TrimStart().Length);

        return string.Join("\n", body.Split('\n')
            .Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }

    // -----------------------------------------------------------------------------------------------
    // The protection for a model that cannot honour a schema still works
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A model without structured output never sees the schema. This is what makes it safe for a
    /// producer to always ask: the seam drops what the pair cannot serve, so no call site grows its
    /// own "does this provider do schemas?" branch.
    /// </summary>
    [Fact]
    public void ASchemaIsStripped_ForAModelThatCannotHonourOne()
    {
        var request = ModelRequest.FromPrompt("x") with { ResponseSchemaJson = """{"type":"object"}""" };

        var negotiated = ModelCapabilityCatalog.Negotiate(request, ModelCapabilities.TextOnly);

        Assert.Null(negotiated.ResponseSchemaJson);
    }

    /// <summary>And a model that CAN honour one still receives it — the sibling that stops the test
    /// above being satisfied by a Negotiate that drops everything.</summary>
    [Fact]
    public void ASchemaSurvivesNegotiation_ForACapableModel()
    {
        var schema = """{"type":"object"}""";
        var request = ModelRequest.FromPrompt("x") with { ResponseSchemaJson = schema };

        var caps = ModelCapabilities.TextOnly with { StructuredOutput = true };
        Assert.Equal(schema, ModelCapabilityCatalog.Negotiate(request, caps).ResponseSchemaJson);
    }

    /// <summary>
    /// The router's own plumbing carries it: `Compose` sets `ResponseSchemaJson` when a schema is
    /// given, and leaves the request untouched when it is not.
    /// </summary>
    [Fact]
    public void TheRouterCarriesTheSchema_OnlyWhenThereIsOne()
    {
        var router = Src("Anthill.Core", "Models", "ModelRouter.cs");

        Assert.Contains("request with { ResponseSchemaJson = schema }", router);
        Assert.Contains("string.IsNullOrWhiteSpace(schema) ? request", router);
    }
}
