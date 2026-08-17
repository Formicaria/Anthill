using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthill.Modules.Reasoning;
using Anthill.SDK.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The provider-adapter conformance matrix. v0.3.8.77 (PLAN.md §2 R1, and its exit gate).
///
/// WHAT R1 ASKED FOR: "every adapter passes the conformance suite or is explicitly marked
/// unsupported for a named capability." The second clause is the substance. Four adapters against
/// eight capabilities is thirty-two questions, and before this file the honest answer to most of
/// them was "probably" — not because the behaviour was wrong but because nobody had written down
/// which cells were proved, which were unprovable offline, and which were genuinely absent.
///
/// WHY A MATRIX AND NOT THIRTY-TWO NEW TESTS. Most cells were already proved. `ProviderWireFormat`
/// keeps encoding out of the adapters precisely so it can be tested without a provider running, and
/// `ProviderWireFormatTests`, `OllamaOpenAiEndpointTests`, `AgentCliTests` and
/// `AgentCliTransportTests` already cover a large part of this. Writing fresh tests over the same
/// ground would be a second implementation of one rule — the thing this repository refuses. So the
/// matrix CITES the test that proves each cell, the citations are checked to resolve, and only the
/// genuinely uncovered cells got new tests. An unproved cell is a failure; an unsupported cell is a
/// sentence someone had to write.
///
/// THE DEFECT THE MATRIX FOUND ON ITS FIRST PASS, and it is the previous release's defect one layer
/// down. `ModelCapabilityCatalog` declares `anthropic` as `Standard`, which includes
/// `StructuredOutput = true`. `Negotiate` therefore KEPT a response schema for Anthropic — and
/// `AnthropicBody` never read the field. The schema was dropped on the floor in silence while the
/// capability report told the operator structured output was supported.
///
/// It could not have been noticed before v0.3.8.76, because until that release no producer ever set
/// `ResponseSchemaJson`. Wiring the coder, planner and strategist made a declaration reachable for
/// the first time since v3.4.0, and the first thing it reached was an adapter that ignored it. The
/// fix is in `AnthropicBody`/`ReadAnthropic` and is proved by the cells below.
/// </summary>
public class AdapterConformanceTests
{
    // -----------------------------------------------------------------------------------------------
    // The matrix
    // -----------------------------------------------------------------------------------------------

    /// <summary>The four transport adapters, by the provider id they stamp on a response.</summary>
    private static readonly string[] Adapters =
        { "ollama", "openai_compatible", "anthropic", "agent_cli" };

    /// <summary>
    /// The eight capabilities R1 names. Each is a question a live failure would need answered before
    /// it could be attributed to the model rather than to the adapter.
    /// </summary>
    private static readonly string[] Capabilities =
    {
        "capability_discovery", "system_prompt_transport", "schema_round_trip", "tool_call_round_trip",
        "cancellation_and_timeout", "provider_model_identity", "token_reporting", "error_classification",
    };

    private enum Verdict
    {
        /// <summary>Proved, by the named test(s).</summary>
        Proven,

        /// <summary>The adapter cannot do this, and the reason is a property of the transport.</summary>
        Unsupported,
    }

    private sealed record Cell(string Adapter, string Capability, Verdict Verdict, string Detail);

    /// <summary>
    /// Thirty-two cells, each decided. `Detail` is a citation for `Proven` and a reason for
    /// `Unsupported`; both are checked below, so neither can rot into a sentence that used to
    /// be true.
    /// </summary>
    private static readonly Cell[] Matrix =
    {
        // ---- ollama ---------------------------------------------------------------------------
        new("ollama", "capability_discovery", Verdict.Proven,
            "OllamaCapabilityCacheTests.AReportedModel_UsesWhatTheRuntimeSaid;"
          + "ModelCapabilityTests.OllamaReportedCapabilities_BeatTheNameTable"),
        new("ollama", "system_prompt_transport", Verdict.Proven,
            "OllamaOpenAiEndpointTests.MessagesKeepTheirRoles_RatherThanBeingFlattened"),
        new("ollama", "schema_round_trip", Verdict.Proven,
            "AdapterConformanceTests.OpenAiBody_CarriesAResponseSchema"),
        new("ollama", "tool_call_round_trip", Verdict.Proven,
            "OllamaOpenAiEndpointTests.AToolCallFromALocalModel_ComesBackAsStructure"),
        new("ollama", "cancellation_and_timeout", Verdict.Proven,
            "AdapterConformanceTests.EveryHttpAdapter_LinksTheAmbientTokenAndSetsADeadline"),
        new("ollama", "provider_model_identity", Verdict.Proven,
            "AdapterConformanceTests.EveryAdapter_StampsItsOwnProviderId"),
        new("ollama", "token_reporting", Verdict.Proven,
            "ProviderWireFormatTests.OpenAi_ReadsContentUsageAndModel;"
          + "ProviderWireFormatTests.MissingUsage_ReadsAsUnknown_NotZero"),
        new("ollama", "error_classification", Verdict.Proven,
            "OllamaOpenAiEndpointTests.AMissingModel_StillSaysHowToPullIt"),

        // ---- openai_compatible ----------------------------------------------------------------
        new("openai_compatible", "capability_discovery", Verdict.Proven,
            "ModelCapabilityTests.AnUnknownProviderAndModel_GetsTheLeastCapableProfile"),
        new("openai_compatible", "system_prompt_transport", Verdict.Proven,
            "AdapterConformanceTests.OpenAiBody_KeepsTheSystemTurnAsAMessage"),
        new("openai_compatible", "schema_round_trip", Verdict.Proven,
            "AdapterConformanceTests.OpenAiBody_CarriesAResponseSchema"),
        new("openai_compatible", "tool_call_round_trip", Verdict.Proven,
            "ProviderWireFormatTests.OpenAi_ToolsAreNestedUnderFunction_WithTheSchemaIntact;"
          + "ProviderWireFormatTests.AReplyOfOnlyToolCalls_IsASuccess_NotAnEmptyResponse"),
        new("openai_compatible", "cancellation_and_timeout", Verdict.Proven,
            "AdapterConformanceTests.EveryHttpAdapter_LinksTheAmbientTokenAndSetsADeadline"),
        new("openai_compatible", "provider_model_identity", Verdict.Proven,
            "ProviderWireFormatTests.OpenAi_ReadsContentUsageAndModel"),
        new("openai_compatible", "token_reporting", Verdict.Proven,
            "ProviderWireFormatTests.MissingUsage_ReadsAsUnknown_NotZero"),
        new("openai_compatible", "error_classification", Verdict.Proven,
            "ProviderWireFormatTests.AMalformedReply_IsAStatus_NotACrash"),

        // ---- anthropic -------------------------------------------------------------------------
        new("anthropic", "capability_discovery", Verdict.Proven,
            "AdapterConformanceTests.AnthropicCapabilities_ComeFromTheCatalog"),
        new("anthropic", "system_prompt_transport", Verdict.Proven,
            "ProviderWireFormatTests.Anthropic_SystemPromptIsLiftedOutOfTheMessages"),
        new("anthropic", "schema_round_trip", Verdict.Proven,
            "AdapterConformanceTests.Anthropic_ASchemaBecomesAForcedToolCall;"
          + "AdapterConformanceTests.Anthropic_TheForcedToolReplyIsUnwrappedIntoContent"),
        new("anthropic", "tool_call_round_trip", Verdict.Proven,
            "ProviderWireFormatTests.Anthropic_ToolsUseInputSchema_NotParameters;"
          + "ProviderWireFormatTests.Anthropic_ReadsTextBlocksAndToolUse"),
        new("anthropic", "cancellation_and_timeout", Verdict.Proven,
            "AdapterConformanceTests.EveryHttpAdapter_LinksTheAmbientTokenAndSetsADeadline"),
        new("anthropic", "provider_model_identity", Verdict.Proven,
            "AdapterConformanceTests.Anthropic_StampsItsProviderAndModel"),
        new("anthropic", "token_reporting", Verdict.Proven,
            "AdapterConformanceTests.Anthropic_ReadsTokenUsage"),
        new("anthropic", "error_classification", Verdict.Proven,
            "AdapterConformanceTests.Anthropic_AMalformedReplyIsAStatus_NotACrash"),

        // ---- agent_cli -------------------------------------------------------------------------
        new("agent_cli", "capability_discovery", Verdict.Proven,
            "AgentCliTests.AnAgentIsReportedCapable_AndOtherProvidersAreLeftAlone"),
        new("agent_cli", "system_prompt_transport", Verdict.Proven,
            "AgentCliTransportTests.ClaudeCode_TakesThePromptOnStdin_AndCarriesNoPromptArgument;"
          + "AgentCliTransportTests.AgentsWithoutAVerifiedStdinMode_KeepTheArgumentTransport"),
        new("agent_cli", "schema_round_trip", Verdict.Unsupported,
            "there is no schema channel. A CLI agent is a process that takes prose on stdin and "
          + "writes prose to stdout; nothing in that transport can bind a reply to a shape, and no "
          + "catalogued agent exposes a flag that would. A caller must parse and re-ask, which is "
          + "what BoundedAgentLoop already does."),
        new("agent_cli", "tool_call_round_trip", Verdict.Unsupported,
            "the agent runs its own tools inside its own process. The colony never sees a tool call "
          + "to dispatch or a result to return — it sees the transcript afterwards. This is the "
          + "reason an agent CLI is dispatched as a TOOL inside a mission rather than routed to as a "
          + "model."),
        new("agent_cli", "cancellation_and_timeout", Verdict.Proven,
            "AgentCliTests.AHangingAgent_IsBoundedByTheConfiguredDeadline"),
        new("agent_cli", "provider_model_identity", Verdict.Proven,
            "AdapterConformanceTests.EveryAdapter_StampsItsOwnProviderId"),
        new("agent_cli", "token_reporting", Verdict.Unsupported,
            "the transport carries no token accounting. An agent CLI reports a transcript, not a "
          + "usage block, so `ModelUsage.Unknown` is the honest value — and it is Unknown rather "
          + "than zero, because zero would read as a free call and silently flatten cost reporting "
          + "for the most expensive calls the colony makes."),
        new("agent_cli", "error_classification", Verdict.Proven,
            "AgentCliTests.AnAgentThatIsNotInstalled_RefusesTypedAndSaysHowToInstallIt"),
    };

    // -----------------------------------------------------------------------------------------------
    // The matrix is complete, and its citations resolve
    // -----------------------------------------------------------------------------------------------

    /// <summary>Every adapter × capability pair is decided exactly once.</summary>
    [Fact]
    public void EveryCell_IsDecidedExactlyOnce()
    {
        var missing = new List<string>();
        var duplicated = new List<string>();

        foreach (var adapter in Adapters)
            foreach (var capability in Capabilities)
            {
                var found = Matrix.Count(c => c.Adapter == adapter && c.Capability == capability);
                if (found == 0) missing.Add($"{adapter}/{capability}");
                if (found > 1) duplicated.Add($"{adapter}/{capability}");
            }

        Assert.True(missing.Count == 0,
            "these adapter/capability cells are undecided: " + string.Join(", ", missing)
          + ". R1's exit gate is that every adapter passes or is explicitly marked unsupported for a "
          + "NAMED capability. An absent cell is neither, and it reads as passing.");
        Assert.True(duplicated.Count == 0, "duplicated cells: " + string.Join(", ", duplicated));
    }

    /// <summary>And the matrix names no adapter or capability outside the two lists.</summary>
    [Fact]
    public void TheMatrix_NamesNothingOutsideItsOwnAxes()
    {
        foreach (var cell in Matrix)
        {
            Assert.Contains(cell.Adapter, Adapters);
            Assert.Contains(cell.Capability, Capabilities);
        }
    }

    /// <summary>
    /// EVERY CITED TEST EXISTS. The assertion with teeth, and the same discipline
    /// `SecurityReviewQueueTests.EveryCitedFile_StillExists` applies to the security review: a
    /// citation that no longer resolves reads as a checked claim and is not one. A renamed test
    /// would otherwise leave a cell saying "proved" with nothing behind it.
    /// </summary>
    [Fact]
    public void EveryProvenCell_CitesATestThatExists()
    {
        var unresolved = new List<string>();

        foreach (var cell in Matrix.Where(c => c.Verdict == Verdict.Proven))
            foreach (var citation in cell.Detail.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = citation.Trim().Split('.');
                Assert.True(parts.Length == 2,
                    $"{cell.Adapter}/{cell.Capability} cites \"{citation}\", which is not Type.Method.");

                var file = Path.Combine(SourceText.RepoRoot(), "tests", "Anthill.Tests", $"{parts[0]}.cs");
                if (!File.Exists(file)) { unresolved.Add($"{citation} (no {parts[0]}.cs)"); continue; }

                // The method name followed by `(` — matches `public void X()` and the async
                // `public async ThreadingTask X()` spelling this suite also uses.
                if (!Regex.IsMatch(File.ReadAllText(file), $@"\b{Regex.Escape(parts[1])}\s*\("))
                    unresolved.Add($"{citation} (no method {parts[1]})");
            }

        Assert.True(unresolved.Count == 0,
            "these conformance cells cite tests that do not exist: " + string.Join("; ", unresolved)
          + ". Either the test was renamed and the citation must follow it, or the cell is claiming "
          + "a proof nobody wrote.");
    }

    /// <summary>
    /// An UNSUPPORTED cell states a reason about the transport, at length. Short is the tell: "not
    /// supported" is a restatement of the verdict, and the whole value of an explicit unsupported
    /// mark is that the next person does not re-derive why.
    /// </summary>
    [Fact]
    public void EveryUnsupportedCell_SaysWhy()
    {
        foreach (var cell in Matrix.Where(c => c.Verdict == Verdict.Unsupported))
            Assert.True(cell.Detail.Length >= 80,
                $"{cell.Adapter}/{cell.Capability} is marked unsupported with only "
              + $"\"{cell.Detail}\". Say what about the transport makes it impossible.");
    }

    /// <summary>
    /// EVERY TRANSPORT ADAPTER IN THE MODULE IS IN THE MATRIX. Without this, a fifth provider is
    /// added, conforms to nothing, and the suite stays green because the matrix never heard of it —
    /// the same shape as a role with no contract, which is what R1's other half was about.
    /// </summary>
    [Fact]
    public void EveryReasoningProviderInTheModule_IsInTheMatrix()
    {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OllamaClient"] = "ollama",
            ["OpenAiCompatibleClient"] = "openai_compatible",
            ["AnthropicClient"] = "anthropic",
            ["AgentCliProvider"] = "agent_cli",
        };

        var implementations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Modules",
                                  "Anthill.Modules.Reasoning"), "*.cs"))
        {
            foreach (Match m in Regex.Matches(SourceText.CodeOnly(File.ReadAllText(file)),
                         @"class\s+(?<type>\w+)\s*:\s*[^{]*\bI(?:Streaming)?ReasoningProvider\b"))
            {
                var type = m.Groups["type"].Value;
                if (type.EndsWith("Factory", StringComparison.Ordinal)) continue;  // makes providers, is not one
                implementations.Add(type);
            }
        }

        var unmatrixed = implementations.Where(t => !declared.ContainsKey(t)).ToList();
        Assert.True(unmatrixed.Count == 0,
            "these reasoning providers exist in the module and are not in the conformance matrix: "
          + string.Join(", ", unmatrixed) + ". Add eight cells, or say why the adapter is exempt.");

        var vanished = declared.Keys.Where(t => !implementations.Contains(t)).ToList();
        Assert.True(vanished.Count == 0,
            "the matrix names adapters that no longer exist: " + string.Join(", ", vanished));
    }

    // -----------------------------------------------------------------------------------------------
    // The cells this file proves itself
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// OpenAI-shaped: a response schema reaches the body as `response_format`.
    ///
    /// `OpenAi_NoTools_OmitsTheKeyEntirely` already proves the key is absent without one. Absence
    /// without presence is half a proof — it passes just as well against a builder that can never
    /// emit the key at all, which is precisely the state Anthropic's builder was in.
    /// </summary>
    [Fact]
    public void OpenAiBody_CarriesAResponseSchema()
    {
        var request = ModelRequest.FromPrompt("hello") with
        {
            ResponseSchemaJson = """{"type":"object","properties":{"x":{"type":"string"}}}""",
        };

        var body = ProviderWireFormat.OpenAiBody(request, "gpt-4o");
        var format = body["response_format"]!.AsObject();

        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        // The schema must arrive as an OBJECT, not as a string containing JSON — the same mistake
        // the tool-schema test guards one field over.
        Assert.Equal("string",
            format["json_schema"]!["schema"]!["properties"]!["x"]!["type"]!.GetValue<string>());
    }

    /// <summary>OpenAI-shaped: the system turn stays a message with its role, rather than being
    /// flattened into the user turn — the transport half of v0.3.8.59's system-channel work.</summary>
    [Fact]
    public void OpenAiBody_KeepsTheSystemTurnAsAMessage()
    {
        var request = new ModelRequest
        {
            Messages = new[]
            {
                new ModelMessage(ModelMessage.System, "operating contract"),
                new ModelMessage(ModelMessage.User, "the goal"),
            },
        };

        var messages = ProviderWireFormat.OpenAiBody(request, "gpt-4o")["messages"]!.AsArray();

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("operating contract", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    /// <summary>
    /// ANTHROPIC: a response schema becomes a forced tool call. This is the cell that was broken.
    ///
    /// Anthropic has no `response_format`. Its documented JSON mode is a tool the model must call,
    /// so the schema becomes `input_schema` and `tool_choice` names it. Before v0.3.8.77 the field
    /// was ignored entirely and the capability catalog said it was supported.
    /// </summary>
    [Fact]
    public void Anthropic_ASchemaBecomesAForcedToolCall()
    {
        var request = ModelRequest.FromPrompt("hello") with
        {
            ResponseSchemaJson = """{"type":"object","properties":{"x":{"type":"string"}}}""",
        };

        var body = ProviderWireFormat.AnthropicBody(request, "claude-sonnet-4");

        var tool = body["tools"]!.AsArray()[0]!.AsObject();
        Assert.Equal(ProviderWireFormat.StructuredOutputToolName, tool["name"]!.GetValue<string>());
        Assert.Equal("string",
            tool["input_schema"]!["properties"]!["x"]!["type"]!.GetValue<string>());

        var choice = body["tool_choice"]!.AsObject();
        Assert.Equal("tool", choice["type"]!.GetValue<string>());
        Assert.Equal(ProviderWireFormat.StructuredOutputToolName, choice["name"]!.GetValue<string>());
    }

    /// <summary>
    /// ANTHROPIC: and the forced call comes back as CONTENT, not as a tool call.
    ///
    /// The other half, and the half whose absence would be invisible: a reply that honoured the
    /// schema perfectly would arrive as a `tool_use` block with empty text, and empty content reads
    /// downstream as "the model said nothing". The answer would be discarded at the last step.
    /// </summary>
    [Fact]
    public void Anthropic_TheForcedToolReplyIsUnwrappedIntoContent()
    {
        var reply = $$"""
            {
              "model": "claude-sonnet-4",
              "stop_reason": "tool_use",
              "content": [
                { "type": "tool_use", "id": "t1", "name": "{{ProviderWireFormat.StructuredOutputToolName}}",
                  "input": { "summary": "done", "proposals": [] } }
              ],
              "usage": { "input_tokens": 11, "output_tokens": 22 }
            }
            """;

        var response = ProviderWireFormat.ReadAnthropic(reply, "claude-sonnet-4");

        Assert.Equal(ModelCallOutcome.Ok, response.Status);
        Assert.Empty(response.ToolCalls);          // it was a schema, not a tool the caller offered
        var parsed = JsonNode.Parse(response.Content)!.AsObject();
        Assert.Equal("done", parsed["summary"]!.GetValue<string>());
    }

    /// <summary>
    /// A REAL tool call is still a tool call. The unwrap must not swallow the tool-calling path —
    /// it keys on the synthetic name, and this is what proves the two stay separate.
    /// </summary>
    [Fact]
    public void Anthropic_AGenuineToolCall_IsNotUnwrapped()
    {
        var reply = """
            {
              "model": "claude-sonnet-4",
              "content": [
                { "type": "tool_use", "id": "t1", "name": "list_directory", "input": { "path": "." } }
              ]
            }
            """;

        var response = ProviderWireFormat.ReadAnthropic(reply, "claude-sonnet-4");

        Assert.Single(response.ToolCalls);
        Assert.Equal("list_directory", response.ToolCalls[0].Name);
    }

    /// <summary>
    /// Schema-plus-tools is never REQUESTED, so the branch that cannot serve both is unreachable.
    ///
    /// `AnthropicBody` binds a schema only when there are no tools, because forcing `tool_choice` at
    /// the synthetic tool would make the caller's real tools unreachable. That is safe only while
    /// nothing sends both, and this is what keeps it true: `GenerateTyped` carries a schema and
    /// never tools, `ToolCallingLoop` carries tools and never a schema.
    /// </summary>
    [Fact]
    public void Anthropic_ASchemaAndToolsAreNeverRequestedTogether()
    {
        var loop = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "ToolCallingLoop.cs")));
        Assert.DoesNotContain("ResponseSchemaJson", loop);

        var router = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Models", "ModelRouter.cs")));

        // Compose builds the schema-bearing request, and it sets no Tools. Sliced by a regex rather
        // than by two IndexOf calls: a `..IndexOf(...)` that finds nothing returns -1 and throws,
        // so the failure would be an exception about a range rather than a statement about tools.
        var compose = Regex.Match(router,
            @"private static ModelRequest Compose.*?\n    \}", RegexOptions.Singleline);

        Assert.True(compose.Success,
            "could not find ModelRouter.Compose — this assertion reads its body, and a rename would "
          + "otherwise make it pass by examining nothing.");
        Assert.DoesNotContain("Tools =", compose.Value);
    }

    /// <summary>ANTHROPIC: token usage is read from its own field names.</summary>
    [Fact]
    public void Anthropic_ReadsTokenUsage()
    {
        var reply = """
            {"model":"claude-sonnet-4","content":[{"type":"text","text":"hi"}],
             "usage":{"input_tokens":11,"output_tokens":22}}
            """;

        var usage = ProviderWireFormat.ReadAnthropic(reply, "claude-sonnet-4").Usage;

        Assert.Equal(11, usage.PromptTokens);
        Assert.Equal(22, usage.CompletionTokens);
        Assert.Equal(33, usage.TotalTokens);
    }

    /// <summary>ANTHROPIC: identity is stamped from the reply, falling back to what was asked for.</summary>
    [Fact]
    public void Anthropic_StampsItsProviderAndModel()
    {
        var served = ProviderWireFormat.ReadAnthropic(
            """{"model":"claude-sonnet-4-served","content":[{"type":"text","text":"hi"}]}""",
            "claude-sonnet-4-requested");

        Assert.Equal("anthropic", served.Provider);
        Assert.Equal("claude-sonnet-4-served", served.Model);   // what ANSWERED, not what was asked

        var silent = ProviderWireFormat.ReadAnthropic(
            """{"content":[{"type":"text","text":"hi"}]}""", "claude-sonnet-4-requested");

        Assert.Equal("claude-sonnet-4-requested", silent.Model);
    }

    /// <summary>ANTHROPIC: a malformed reply is a status, not a crash — and still carries identity,
    /// because an error whose provider is unknown cannot open the right circuit breaker.</summary>
    [Fact]
    public void Anthropic_AMalformedReplyIsAStatus_NotACrash()
    {
        var response = ProviderWireFormat.ReadAnthropic("{ not json", "claude-sonnet-4");

        Assert.Equal(ModelCallOutcome.Error, response.Status);
        Assert.Equal("anthropic", response.Provider);
        Assert.Equal("claude-sonnet-4", response.Model);
    }

    /// <summary>ANTHROPIC: the capability profile is the catalog's, and it claims what the adapter
    /// now actually does. This is the pairing that was false until v0.3.8.77.</summary>
    [Fact]
    public void AnthropicCapabilities_ComeFromTheCatalog()
    {
        var caps = ModelCapabilityCatalog.For("anthropic", "claude-sonnet-4");

        Assert.True(caps.StructuredOutput);
        Assert.True(caps.ToolCalling);

        // …and because it claims structured output, Negotiate keeps a schema for it. That is the
        // exact path that used to end at a builder which ignored the field.
        var request = ModelRequest.FromPrompt("x") with { ResponseSchemaJson = """{"type":"object"}""" };
        Assert.NotNull(ModelCapabilityCatalog.Negotiate(request, caps).ResponseSchemaJson);
    }

    /// <summary>
    /// Every HTTP adapter links the ambient cancellation token AND sets its own deadline.
    ///
    /// Asserted on source because the property is structural: an adapter that forgets the ambient
    /// token keeps running after the mission is cancelled, and one that forgets `CancelAfter`
    /// inherits only `HttpClient.Timeout`, which is a socket timeout rather than a call deadline.
    /// Neither failure is visible in a passing test of the happy path.
    /// </summary>
    [Fact]
    public void EveryHttpAdapter_LinksTheAmbientTokenAndSetsADeadline()
    {
        foreach (var file in new[] { "OllamaProvider.cs", "ProviderClients.cs" })
        {
            var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
                SourceText.RepoRoot(), "src", "Anthill.Modules", "Anthill.Modules.Reasoning", file)));

            Assert.Contains("ModelCallScope.Current", source);
            Assert.Contains("CreateLinkedTokenSource", source);
            Assert.Contains("CancelAfter", source);
        }
    }

    /// <summary>
    /// Every adapter stamps its own provider id on what it returns.
    ///
    /// Read from source for the two adapters whose identity is a literal at the seam: without it an
    /// error cannot be attributed to a provider, and the circuit breaker keys on the route.
    /// </summary>
    [Fact]
    public void EveryAdapter_StampsItsOwnProviderId()
    {
        var ollama = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Modules", "Anthill.Modules.Reasoning", "OllamaProvider.cs")));
        Assert.Contains("\"ollama\"", ollama);

        var agent = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Modules", "Anthill.Modules.Reasoning", "AgentCliProvider.cs")));
        Assert.Contains("Provider = _agent.Id", agent);
        Assert.Contains("Model = _agent.DisplayName", agent);
    }

    /// <summary>
    /// The agent CLI reports UNKNOWN usage, not zero — the cell marked unsupported, asserted rather
    /// than assumed. Zero would read as a free call and flatten cost reporting for the most
    /// expensive calls the colony makes.
    /// </summary>
    [Fact]
    public void TheAgentCli_ReportsUnknownUsage_NotZero()
    {
        Assert.Null(ModelUsage.Unknown.PromptTokens);
        Assert.Null(ModelUsage.Unknown.TotalTokens);

        var agent = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Modules", "Anthill.Modules.Reasoning", "AgentCliProvider.cs")));
        Assert.DoesNotContain("new ModelUsage(0", agent);
    }
}
