using Anthill.Modules.Reasoning;
using Anthill.SDK.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Installed command-line agents as reasoning providers. v3.8.39.
///
/// What is testable here is everything that does not require five vendors' tools to be installed:
/// the catalogue's shape, how a prompt becomes an argument vector, that the factory keeps its
/// no-I/O promise, and that a missing agent fails in the colony's typed vocabulary rather than by
/// throwing. Whether Claude Code itself answers correctly is Anthropic's test, not this repo's.
/// </summary>
public class AgentCliTests
{
    [Fact]
    public void EveryCatalogueEntry_IsCompleteEnoughToInstallAndRun()
    {
        Assert.NotEmpty(AgentCliCatalog.All);

        foreach (var a in AgentCliCatalog.All)
        {
            Assert.StartsWith(AgentCliCatalog.IdPrefix, a.Id, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(a.DisplayName), $"{a.Id} has no display name");
            Assert.False(string.IsNullOrWhiteSpace(a.Vendor), $"{a.Id} does not say whose account it uses");
            Assert.False(string.IsNullOrWhiteSpace(a.InstallCommand), $"{a.Id} cannot be installed");
            Assert.False(string.IsNullOrWhiteSpace(a.AuthCommand), $"{a.Id} cannot be signed in to");
            Assert.False(string.IsNullOrWhiteSpace(a.DocsUrl), $"{a.Id} has nowhere to read about it");

            // A bare executable name, resolved against PATH. A path separator here would make the
            // catalogue depend on one machine's layout.
            Assert.DoesNotContain("/", a.Binary, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", a.Binary, StringComparison.Ordinal);

            // Without a placeholder the operator's text would never reach the agent, and the call
            // would look like it worked while asking the agent nothing.
            Assert.Contains(a.PromptArgs, arg => arg.Contains("{prompt}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AgentIds_AreUnique()
    {
        var ids = AgentCliCatalog.All.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// An agent id may never collide with a reasoning provider id. v3.8.39.
    ///
    /// Both are offered in the same routing list and a role's route stores ONE provider string, so a
    /// collision would not fail — it would silently route an ant to the wrong thing, and the symptom
    /// would be an ant that answers plausibly while never running the tool it was given. The
    /// `agent:` prefix is what prevents it, and this is the test that keeps the prefix load-bearing
    /// rather than decorative.
    /// </summary>
    [Fact]
    public void AgentIds_NeverCollideWithAReasoningProviderId()
    {
        var providerIds = ProviderCatalog.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var a in AgentCliCatalog.All)
            Assert.DoesNotContain(a.Id, providerIds);
    }

    /// <summary>
    /// Every agent carries a non-empty model name, and that is load-bearing.
    ///
    /// `ModelRouter.GetClient` treats a NON-KEYED provider with an empty model as a local model
    /// needing resolution, and hands it to `LocalModelResolver` — which would ask Ollama to resolve
    /// a model for Claude Code, a question with no possible answer. The catalogue entry the API
    /// composes uses DisplayName as `default_model` precisely so that branch is never reached, so an
    /// agent whose name were blank would route into it.
    /// </summary>
    [Fact]
    public void EveryAgent_HasAModelNameTheRouterCanCarry()
    {
        foreach (var a in AgentCliCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.DisplayName));
            Assert.Equal(a.DisplayName.Trim(), a.DisplayName);
        }
    }

    /// <summary>
    /// The injection guard. Operator text is passed as ONE argv entry, never spliced into a string
    /// a shell could read.
    ///
    /// A prompt legitimately contains quotes, semicolons, newlines and backticks — "fix the bug in
    /// `main`; it fails when x=1" is an ordinary request. Joined into a command line it is three
    /// commands. The argument vector is what makes it text, and this test pins that the dangerous
    /// characters survive intact inside a single element rather than being escaped, stripped, or
    /// split across several.
    /// </summary>
    [Fact]
    public void APromptWithShellMetacharacters_StaysOneArgument()
    {
        var agent = AgentCliCatalog.All.First();
        const string nasty = "fix `main`; rm -rf / && echo \"done\" $(whoami)\nsecond line";

        var args = AgentCliCatalog.BuildArgs(agent, nasty);

        var carrying = args.Where(a => a.Contains("rm -rf", StringComparison.Ordinal)).ToList();
        Assert.Single(carrying);
        Assert.Equal(nasty, carrying[0]);
        Assert.DoesNotContain(args, a => a.Contains("{prompt}", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFactory_ServesEveryCataloguedAgent_AndNothingElse()
    {
        var factory = new AgentCliProviderFactory();

        foreach (var a in AgentCliCatalog.All)
            Assert.True(factory.CanServe(a.Id), $"{a.Id} is catalogued but the factory will not serve it");

        Assert.False(factory.CanServe("ollama"));
        Assert.False(factory.CanServe("openai"));
        Assert.False(factory.CanServe(AgentCliCatalog.IdPrefix + "no-such-agent"));
    }

    /// <summary>
    /// Create must not touch PATH, start a process or probe anything.
    ///
    /// IReasoningProviderFactory says so explicitly, and the reason is concrete: providers are
    /// built on the mission hot path, so a factory that probed here would put a process launch in
    /// front of every keyed call. Asserted by building a provider for an agent that certainly is
    /// not installed — if Create probed, this would be slow or throw instead of returning.
    /// </summary>
    [Fact]
    public void Create_DoesNoIo_EvenForAnAgentThatIsNotInstalled()
    {
        var factory = new AgentCliProviderFactory();
        var agent = AgentCliCatalog.All.First();
        var ctx = new ReasoningProviderContext(agent.Id, agent.DisplayName, null, "", new DefaultReasoningRuntimeOptions());

        var started = DateTime.UtcNow;
        var provider = factory.Create(ctx);
        var took = DateTime.UtcNow - started;

        Assert.NotNull(provider);
        Assert.True(took < TimeSpan.FromSeconds(1), $"Create took {took.TotalMilliseconds:0}ms — it is doing I/O");
    }

    /// <summary>
    /// A missing agent is a TYPED refusal that names the remedy — never an exception, and never a
    /// sentinel string a caller has to parse.
    ///
    /// This is the whole reason the colony can run without any provider at all: an ant that gets
    /// NotAvailable can route elsewhere. One that got a thrown exception would take the task with
    /// it, and one that got text starting "ERROR:" would be guessing.
    /// </summary>
    [Fact]
    public void AnAgentThatIsNotInstalled_RefusesTypedAndSaysHowToInstallIt()
    {
        var absent = new AgentCli
        {
            Id = AgentCliCatalog.IdPrefix + "definitely-not-installed",
            DisplayName = "Definitely Not Installed",
            Vendor = "test",
            Binary = "anthill-no-such-binary-" + Guid.NewGuid().ToString("N"),
            PromptArgs = new[] { "-p", "{prompt}" },
            InstallCommand = "npm install -g nothing",
            AuthCommand = "nothing login",
            DocsUrl = "https://example.invalid",
        };

        var response = new AgentCliProvider(absent).Send(ModelRequest.FromPrompt("hello"));

        Assert.Equal(ModelCallOutcome.NotAvailable, response.Status);
        Assert.False(response.Ok);
        Assert.Contains("npm install -g nothing", response.Content, StringComparison.Ordinal);
        Assert.Equal(absent.Id, response.Provider);
    }

    /// <summary>
    /// The factory honours the operator's configured per-call deadline. v3.8.39.
    ///
    /// It shipped with a private ten-minute constant, and the first LIVE test found why that is
    /// wrong: `opencode run` did not return, and the request sat for the whole ten minutes with
    /// nothing to do but wait. `ModelCallTimeoutSeconds` is read on every request precisely so a
    /// colony can bound a slow provider, and a provider that ignores it is a setting that silently
    /// does nothing — which is the failure IReasoningRuntimeOptions exists to prevent.
    ///
    /// Asserted through the provider's own behaviour rather than by reading a private field: a
    /// deliberately tiny deadline against a process that never returns must come back quickly.
    ///
    /// The sleep runs via the shell and the prompt is NOT interpolated into it. The first version
    /// of this test passed `{prompt}` as sleep's second argument, which made it exit instantly on
    /// "invalid time interval" — fast, refused, and never once exercising the deadline. It would
    /// have passed for entirely the wrong reason.
    /// </summary>
    [Fact]
    public void AHangingAgent_IsBoundedByTheConfiguredDeadline()
    {
        var hangs = new AgentCli
        {
            Id = AgentCliCatalog.IdPrefix + "hangs",
            DisplayName = "Hangs",
            Vendor = "test",
            Binary = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            PromptArgs = OperatingSystem.IsWindows()
                ? new[] { "/c", "timeout /t 30 /nobreak" }
                : new[] { "-c", "sleep 30" },
            InstallCommand = "n/a",
            AuthCommand = "n/a",
            DocsUrl = "https://example.invalid",
        };

        var started = DateTime.UtcNow;
        var response = new AgentCliProvider(hangs, TimeSpan.FromSeconds(2)).Send(ModelRequest.FromPrompt("x"));
        var took = DateTime.UtcNow - started;

        // Either it timed out, or the platform had no such binary — both are typed refusals, and
        // neither may hang. The DURATION is the assertion that matters.
        Assert.False(response.Ok);
        Assert.True(took < TimeSpan.FromSeconds(20), $"took {took.TotalSeconds:0}s — the deadline was not applied");
    }

    [Fact]
    public void AnEmptyPrompt_IsRefusedBeforeAnythingIsStarted()
    {
        var agent = AgentCliCatalog.All.First();
        var response = new AgentCliProvider(agent).Send(ModelRequest.FromPrompt("   "));

        Assert.Equal(ModelCallOutcome.ConfigError, response.Status);
    }

    /// <summary>
    /// Scanning must never throw, whatever the host has or has not got. It runs behind a console
    /// poll, and a probe that threw would take the panel with it.
    /// </summary>
    [Fact]
    public void Scanning_ReportsEveryAgent_AndNeverThrows()
    {
        var statuses = AgentCliDiscovery.Scan(force: true);

        Assert.Equal(AgentCliCatalog.All.Count, statuses.Count);

        foreach (var s in statuses)
        {
            // Every agent is either usable, or unusable WITH A REASON. "Not installed" and
            // "installed but broken" need different instructions, so neither may be silent.
            if (!s.Installed) Assert.False(string.IsNullOrWhiteSpace(s.Unavailable), $"{s.Agent.Id} is unavailable and does not say why");
            else Assert.Null(s.Unavailable);
        }
    }
}
