using Anthill.Core.Models;
using Anthill.SDK.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.5 — the phase-2 success criterion, as a test rather than an aspiration.
///
/// "The core can run without any AI provider" was in the refactor plan from the start, and until
/// this release it was not merely untested but impossible: <c>ModelRouter</c> named
/// <c>OllamaClient</c>, <c>OpenAiCompatibleClient</c> and <c>AnthropicClient</c> directly, so the
/// core could not COMPILE without every provider implementation present, let alone run without one.
///
/// These tests resolve against an EXPLICIT factory list rather than emptying the global registry.
/// The obvious version — <c>ReasoningProviders.Reset()</c> then assert — is a trap: the registry is
/// process-global and xUnit runs collections in parallel, so emptying it opens a window in which
/// unrelated tests see no module and fail somewhere else, intermittently. Passing the list in
/// removes the shared state from the test instead of trying to synchronise around it, and still
/// runs the real resolution path.
/// </summary>
public class CoreWithoutProviderTests
{
    private static readonly IReasoningProviderFactory[] NoModules = Array.Empty<IReasoningProviderFactory>();

    private static IModelClient Resolve(params IReasoningProviderFactory[] factories) =>
        ReasoningProviders.ResolveFrom(factories, "ollama", "llama3.1:8b", null, "http://localhost:11434");

    [Fact]
    public void With_no_module_a_model_call_is_refused_rather_than_throwing()
    {
        var response = ReasoningProviders
            .ResolveFrom(NoModules, "ollama", "llama3.1:8b", null, "http://localhost:11434")
            .Send(ModelRequest.FromPrompt("hello"));

        // Returned, not thrown. A client never throws across the ant boundary: an ant receiving
        // Error already knows how to report a degraded generation, and the mission evaluator
        // already refuses to mark a mission verified on one. Throwing would bypass all of it.
        Assert.Equal(ModelCallOutcome.Error, response.Status);
        Assert.Contains("no reasoning module is registered", response.Content);
    }

    /// <summary>
    /// The two ways to have no provider are told apart, because they need different fixes: one is
    /// "this build ships without AI", the other is "you configured a provider nobody serves".
    /// </summary>
    [Fact]
    public void A_module_that_serves_nothing_gives_a_different_reason()
    {
        var response = ReasoningProviders
            .ResolveFrom(new[] { new ServesNothingFactory() }, "some-future-provider", "m", null, "")
            .Send(ModelRequest.FromPrompt("x"));

        Assert.Contains("no registered reasoning module serves", response.Content);
    }

    [Fact]
    public void A_registered_factory_is_used_in_preference_to_the_refusal()
    {
        var response = Resolve(new StubFactory()).Send(ModelRequest.FromPrompt("hi"));

        Assert.Equal(ModelCallOutcome.Ok, response.Status);
        Assert.Equal("stubbed", response.Content);
    }

    /// <summary>
    /// Later registrations win. The useful override — a local mock, a recording proxy — is
    /// registered after the real module, and a registry keyed by provider id would have made that a
    /// silent overwrite instead of a deliberate precedence.
    /// </summary>
    [Fact]
    public void The_most_recently_registered_factory_takes_precedence()
    {
        var response = Resolve(new StubFactory(), new StubFactory("overridden"))
            .Send(ModelRequest.FromPrompt("hi"));

        Assert.Equal("overridden", response.Content);
    }

    /// <summary>
    /// The core hands over LIVE options, not the standalone default a directly-constructed client
    /// falls back to — so an operator lowering the call timeout affects the next call rather than
    /// the next restart.
    /// </summary>
    [Fact]
    public void The_core_passes_live_runtime_options_to_a_factory()
    {
        var factory = new StubFactory();

        Resolve(factory);

        Assert.NotNull(factory.LastContext);
        Assert.IsNotType<DefaultReasoningRuntimeOptions>(factory.LastContext!.Options);
        Assert.Equal(AnthillRuntimeTimeout, factory.LastContext.Options.ModelCallTimeoutSeconds);
    }

    private static int AnthillRuntimeTimeout => Anthill.Core.Configuration.AnthillRuntime.ModelCallTimeoutSeconds;

    /// <summary>
    /// A probe reports null — not an empty capability set — for a provider it cannot describe, so
    /// the core falls back to the declared name table instead of believing the model supports
    /// nothing. Conflating those two answers is the v3.8.2 defect: five roles alarmed as broken on
    /// every restart for a model that reports tools and thinking.
    /// </summary>
    [Fact]
    public void A_probe_says_i_dont_know_rather_than_it_supports_nothing()
    {
        var probe = new Anthill.Modules.Reasoning.OllamaCapabilityProbe("http://127.0.0.1:1");

        Assert.Null(probe.For("openai", "gpt-4o"));
        Assert.Empty(probe.Snapshot("anthropic"));
    }

    private sealed class ServesNothingFactory : IReasoningProviderFactory
    {
        public bool CanServe(string providerId) => false;
        public IReasoningProvider Create(ReasoningProviderContext context) =>
            throw new InvalidOperationException("must never be called");
    }

    private sealed class StubFactory : IReasoningProviderFactory
    {
        private readonly string _content;
        public StubFactory(string content = "stubbed") => _content = content;
        public ReasoningProviderContext? LastContext { get; private set; }

        public bool CanServe(string providerId) => true;

        public IReasoningProvider Create(ReasoningProviderContext context)
        {
            LastContext = context;
            return new StubProvider(_content);
        }
    }

    private sealed class StubProvider : IReasoningProvider
    {
        private readonly string _content;
        public StubProvider(string content) => _content = content;
        public ModelResponse Send(ModelRequest request, int retries = 2) =>
            new() { Status = ModelCallOutcome.Ok, Content = _content };
    }
}
