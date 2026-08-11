using System.Text.Json;
using Anthill.Core.Configuration;
using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The Ollama model is a default for the LOCAL provider and for nothing else. v0.3.8.41.
///
/// Routing every ant to Claude Code produced routes reading `agent:claude-code : gemma4:31b` — an
/// agent paired with a local model tag it has never heard of and cannot serve. Two separate lines
/// did it, both spelling `?? AnthillRuntime.OllamaModel`:
///
///   • RoleRoute defaulted a stored route's missing model to the Ollama tag.
///   • GetClient defaulted an unknown provider's model to the Ollama tag.
///
/// It was never only an agent problem. A keyed OpenAI route with no model would have carried
/// gemma4:31b just as happily, and the symptom — a provider running fine while the console named a
/// model nothing was using — is the kind that reads as a display bug for a long time.
///
/// These assert through the ROUTER rather than by reading fields, because the defect was in what
/// the router returned, and a test that re-implemented the defaulting rule would agree with
/// whichever version of it was written last.
///
/// SETUP GOES THROUGH SETTINGS, NOT THE FIELD. The first draft of this file did
/// `AnthillRuntime.OllamaModel = "gemma4:31b"` and then wrote a route — and the route write is
/// ApplySettingsUpdate, which ends in ProjectConfig, which re-reads OllamaModel from Config. The
/// assignment was erased by the next line of the test. AnthillRuntime says this in as many words
/// at the top of ProjectConfig: "a direct static-field set before that point would otherwise be
/// silently overwritten right here."
///
/// One of the three tests failed loudly and two passed, which is the part worth keeping. Both
/// survivors asserted `NotEqual("gemma4:31b")` against a value that was never gemma4:31b, so they
/// agreed with the fix while testing nothing about it — they would have passed against the
/// unfixed router too. They now assert that a non-local route's model is EMPTY, which is a claim
/// about the behaviour rather than about a string that happened not to be there.
/// </summary>
[Collection("Autonomy")]
public class RouteModelDefaultTests : IDisposable
{
    private readonly Dictionary<string, Dictionary<string, string>> _routes =
        AnthillRuntime.ModelRouting.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
    private readonly string _ollamaModel = AnthillRuntime.OllamaModel;

    // These are process-wide statics AND they persist to config.json, so anything set here is put
    // back — the shared-static leak this repository serialises suites for, plus a file on the
    // operator's disk. Restored through the same production path used to set them, which is the only
    // restore that actually holds: assigning the field back would be undone by the next projection.
    public void Dispose() => Apply(_routes, _ollamaModel);

    private static ModelRouter Router() => new();

    /// <summary>
    /// Written through ApplySettingsUpdate — the path POST /settings uses — rather than by assigning
    /// the statics. ModelRouting is `private set` for exactly that reason, and OllamaModel, though
    /// public, is re-projected from Config on every settings write, so a direct set survives only
    /// until the next one.
    ///
    /// Both values go in ONE update because the route write is itself a projection: applying them
    /// separately means the second erases the first.
    /// </summary>
    private static void Apply(Dictionary<string, Dictionary<string, string>> routes, string ollamaModel) =>
        AnthillRuntime.ApplySettingsUpdate(new Dictionary<string, JsonElement>
        {
            ["model_routes"] = JsonSerializer.SerializeToElement(routes),
            ["ollama_model"] = JsonSerializer.SerializeToElement(ollamaModel),
        });

    /// <summary>
    /// Configures one route and the local default model together, then returns the local default the
    /// runtime actually ended up with.
    ///
    /// It returns the live value rather than echoing the argument because ANTHILL_OLLAMA_MODEL
    /// outranks config: on a host that sets it, these tests should still be asserting about the
    /// model the colony would really use. The caller checks it is non-empty, which is what proves
    /// the setup took at all — an empty local default would make the local-provider case pass
    /// vacuously.
    /// </summary>
    private static string Route(string role, string provider, string? model, string localModel = "gemma4:31b")
    {
        var entry = new Dictionary<string, string> { ["provider"] = provider };
        if (model is not null) entry["model"] = model;
        Apply(new Dictionary<string, Dictionary<string, string>> { [role] = entry }, localModel);
        return AnthillRuntime.OllamaModel;
    }

    /// <summary>
    /// The case that produced the bug report: a provider that is not the local one, and no model.
    /// It must NOT inherit the Ollama tag.
    /// </summary>
    [Fact]
    public void ANonLocalProviderWithNoModel_DoesNotInheritTheOllamaTag()
    {
        var local = Route("coder", "agent:claude-code", model: null);
        Assert.False(string.IsNullOrEmpty(local), "the local default must be set for this to prove anything");

        var (provider, model) = Router().RoleRoute("coder");

        Assert.Equal("agent:claude-code", provider);
        Assert.NotEqual(local, model);
        Assert.True(string.IsNullOrEmpty(model),
            $"expected the provider to decide its own model, got '{model}'");
    }

    /// <summary>
    /// The same rule protects keyed providers, which is how we know the fix is about PROVIDERS and
    /// not a special case bolted on for agents.
    /// </summary>
    [Fact]
    public void AKeyedProviderWithNoModel_DoesNotInheritTheOllamaTag()
    {
        var local = Route("coder", "openai", model: null);
        Assert.False(string.IsNullOrEmpty(local), "the local default must be set for this to prove anything");

        var (_, model) = Router().RoleRoute("coder");

        Assert.NotEqual(local, model);
        Assert.True(string.IsNullOrEmpty(model),
            $"expected the provider to decide its own model, got '{model}'");
    }

    /// <summary>
    /// The LOCAL provider still gets the local default. Removing that would be the opposite
    /// regression, and it is the behaviour the rest of the colony has always relied on.
    /// </summary>
    [Fact]
    public void TheLocalProviderWithNoModel_StillGetsTheLocalDefault()
    {
        var local = Route("coder", AnthillRuntime.DefaultModelProvider, model: null);
        Assert.False(string.IsNullOrEmpty(local), "the local default must be set for this to prove anything");

        var (_, model) = Router().RoleRoute("coder");

        Assert.Equal(local, model);
    }

    /// <summary>An explicitly chosen model is never second-guessed, whoever serves it.</summary>
    [Theory]
    [InlineData("agent:claude-code", "Claude Code")]
    [InlineData("openai", "gpt-4o")]
    [InlineData("ollama", "qwen3.6:27b")]
    public void AnExplicitModel_IsAlwaysKept(string provider, string model)
    {
        Route("coder", provider, model);

        var (p, m) = Router().RoleRoute("coder");

        Assert.Equal(provider, p);
        Assert.Equal(model, m);
    }
}
