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
            Assert.False(string.IsNullOrWhiteSpace(a.PackageManager), $"{a.Id} names no package manager");
            Assert.False(string.IsNullOrWhiteSpace(a.Package), $"{a.Id} names no package");
            Assert.Contains(a.PackageManager, new[] { "npm", "pip" });
            Assert.False(string.IsNullOrWhiteSpace(AgentCliCatalog.InstallHint(a)), $"{a.Id} has no install hint");
            Assert.False(string.IsNullOrWhiteSpace(a.AuthCommand), $"{a.Id} cannot be signed in to");
            Assert.False(string.IsNullOrWhiteSpace(a.DocsUrl), $"{a.Id} has nowhere to read about it");

            // A bare executable name, resolved against PATH. A path separator here would make the
            // catalogue depend on one machine's layout.
            Assert.DoesNotContain("/", a.Binary, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", a.Binary, StringComparison.Ordinal);

            // EVERY AGENT HAS A CHANNEL FOR THE PROMPT — argv or stdin, and exactly one of them.
            //
            // v0.3.8.67: this used to require `{prompt}` in PromptArgs, which was the right check
            // while argv was the only transport. It became wrong when the prompt moved to stdin for
            // Claude Code, and it is worth stating what the invariant actually was: not "there is a
            // placeholder" but "the operator's text reaches the agent". A call with neither channel
            // looks like it worked while asking the agent nothing.
            //
            // BOTH is also refused. A placeholder alongside stdin would send the text twice — once
            // safely and once as the argument whose leading `---` is what broke the transport.
            var inArgv = a.PromptArgs.Any(arg => arg.Contains("{prompt}", StringComparison.Ordinal));
            Assert.True(inArgv ^ a.PromptOnStdin,
                $"{a.Id} must carry the prompt in EXACTLY ONE channel: {{prompt}} in PromptArgs, or "
              + $"PromptOnStdin. argv={inArgv}, stdin={a.PromptOnStdin}.");
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
    /// <remarks>
    /// v0.3.8.67 — asserted per TRANSPORT, and the stdin case is the stronger one.
    ///
    /// This used to take `All.First()` and require the prompt to be one argument. That agent is now
    /// Claude Code, whose prompt travels on stdin, so the assertion found nothing and failed — while
    /// the property it cares about had become MORE true, not less: text that is never an argument
    /// cannot be split, escaped or mangled by an argument vector at all.
    ///
    /// So both transports are checked, over every agent rather than whichever happens to be first.
    /// A test pinned to `First()` is one catalogue reordering away from testing something else.
    /// </remarks>
    [Fact]
    public void APromptWithShellMetacharacters_ReachesTheAgentIntact()
    {
        const string nasty = "fix `main`; rm -rf / && echo \"done\" $(whoami)\nsecond line";

        foreach (var agent in AgentCliCatalog.All)
        {
            var args = AgentCliCatalog.BuildArgs(agent, nasty);
            var carrying = args.Where(a => a.Contains("rm -rf", StringComparison.Ordinal)).ToList();

            if (agent.PromptOnStdin)
                Assert.True(carrying.Count == 0,
                    $"{agent.Id} takes the prompt on stdin, so it must not ALSO be an argument — "
                  + "two channels is how the text arrives twice and once as an option.");
            else
            {
                Assert.Single(carrying);
                Assert.Equal(nasty, carrying[0]);
            }

            Assert.DoesNotContain(args, a => a.Contains("{prompt}", StringComparison.Ordinal));
        }
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
            PackageManager = "npm",
            Package = "nothing",
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
            PackageManager = "npm",
            Package = "n/a",
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

    /// <summary>
    /// Agents install into Anthill's OWN directory, never the global prefix. v0.3.8.41.
    ///
    /// The first version ran `npm install -g`, whose destination on a normal Linux host is
    /// /usr/lib/node_modules — root-owned. Every install failed with EACCES for anyone not running
    /// Anthill as root, and the remedy on offer was "be root", which is not one.
    ///
    /// Asserted on the argument vector rather than by installing anything: the property that
    /// matters is that the destination is under the user's home and is passed explicitly, and that
    /// is decidable without a network or a package manager.
    /// </summary>
    [Fact]
    public void AgentsInstall_IntoAnthillsOwnDirectory_NotTheGlobalPrefix()
    {
        var home = AgentCliInstaller.AgentHome;

        Assert.Contains(".anthill", home, StringComparison.Ordinal);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), home, StringComparison.Ordinal);

        // Discovery must search where the installer writes, or an agent installs and then reports
        // as missing — the worst of both outcomes.
        Assert.Contains(AgentCliInstaller.BinDirectories(),
            d => d.StartsWith(home, StringComparison.Ordinal));
    }

    /// <summary>
    /// A bare binary name resolves against Anthill's bin directories before PATH, because agents
    /// installed outside the global prefix are deliberately NOT on the operator's PATH.
    /// </summary>
    [Fact]
    public void ResolvingABinary_LooksInAnthillsBinDirectories()
    {
        // A name nothing has installed falls through unchanged, so the caller still gets the OS's
        // own PATH resolution and its error, rather than a fabricated path that cannot exist.
        var unknown = "anthill-no-such-binary-" + Guid.NewGuid().ToString("N");
        Assert.Equal(unknown, AgentCliDiscovery.Resolve(unknown));

        // Anything already carrying a path separator is returned untouched — a caller that knows
        // exactly what it wants is never second-guessed.
        var explicitPath = Path.Combine(Path.GetTempPath(), "some", "tool");
        Assert.Equal(explicitPath, AgentCliDiscovery.Resolve(explicitPath));
    }

    /// <summary>
    /// An installed agent is CAPABLE, and the fitness check must know it. v0.3.8.41.
    ///
    /// ModelCapabilityCatalog matches model-name fragments then a provider table, and falls through
    /// to TextOnly for anything unknown. Routing an ant to Claude Code therefore reported it as
    /// missing tool calling and structured output — the boot log warning that `ui_cartographer` was
    /// routed to something "missing tool calling" when the thing was a tool-calling coding agent.
    ///
    /// Null for a provider this probe does not serve, which the interface treats as the meaningful
    /// difference: "I don't know" falls back to the name table, "supports nothing" is believed.
    /// Answering for Ollama here would override its DISCOVERED capabilities with a guess.
    /// </summary>
    [Fact]
    public void AnAgentIsReportedCapable_AndOtherProvidersAreLeftAlone()
    {
        var probe = new AgentCapabilityProbe();
        var agent = AgentCliCatalog.All.First();

        var caps = probe.For(agent.Id, agent.DisplayName);
        Assert.NotNull(caps);
        Assert.True(caps!.ToolCalling);
        Assert.True(caps.StructuredOutput);
        Assert.True(caps.Reasoning);
        // Unknown, not small: it depends on the model configured inside the agent, which Anthill
        // does not know and must not invent.
        Assert.Null(caps.ContextWindowTokens);

        Assert.Null(probe.For("ollama", "gemma4:31b"));
        Assert.Null(probe.For("openai", "gpt-4o"));
        Assert.Empty(probe.Snapshot("ollama"));
        Assert.NotEmpty(probe.Snapshot(agent.Id));
    }

    [Fact]
    public void AnEmptyPrompt_IsRefusedBeforeAnythingIsStarted()
    {
        var agent = AgentCliCatalog.All.First();
        var response = new AgentCliProvider(agent).Send(ModelRequest.FromPrompt("   "));

        Assert.Equal(ModelCallOutcome.ConfigError, response.Status);
    }

    // ---- Confinement -------------------------------------------------------------------------
    //
    // v0.3.8.41. AgentCliProvider has always taken a workingDirectory, documented as what keeps a
    // writing agent inside the same boundary as every other actor. The factory — the only place
    // production ever built one — did not pass it. Null meant ProcessStartInfo.WorkingDirectory was
    // never set, so the agent inherited the API host's directory: the operator's live checkout.
    //
    // Routing an ant to Claude Code therefore handed a tool with Writes = true a shell in the source
    // tree, going around SandboxWorkspace, WorkspacePathGuard, PatchSet review and the
    // approve-then-apply gate in one step, and silently — Anthill never saw the edits at all.
    //
    // These assert the two halves separately, because they failed independently: that a writing
    // agent REFUSES when unconfined, and that a confined one really runs in the directory it was
    // given. Only the second is a claim about the operating system, so only it starts a process.

    /// <summary>An agent that writes must not start at all when it has nowhere to be confined to.</summary>
    [Fact]
    public void AWritingAgentWithNoWorkspace_RefusesAndNeverStarts()
    {
        // A marker is the proof. Asserting the typed refusal alone would also pass if the process
        // ran and the refusal came afterwards, which is the case that matters: an agent that writes
        // has already written by the time it exits.
        var marker = Path.Combine(Path.GetTempPath(), "anthill-confinement-" + Guid.NewGuid().ToString("N"));
        var writes = Shell("writes-unconfined", Touch(marker), writes: true);

        var response = new AgentCliProvider(writes).Send(ModelRequest.FromPrompt("x"));

        Assert.Equal(ModelCallOutcome.ConfigError, response.Status);
        Assert.False(File.Exists(marker), "the agent ran despite having no workspace to be confined to");
        Assert.Contains("workspace", response.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An agent that only ANSWERS is not gated, because its working directory is uninteresting.
    ///
    /// Worth pinning: the temptation when fixing this was to refuse for every agent, which would
    /// have broken every colony that routes a read-only agent, and would have been the wrong
    /// analysis besides — the hazard is writing outside a boundary, not running.
    /// </summary>
    [Fact]
    public void AReadOnlyAgentWithNoWorkspace_IsNotGated()
    {
        var reads = Shell("reads", Echo("hello"), writes: false);

        var response = new AgentCliProvider(reads).Send(ModelRequest.FromPrompt("x"));

        Assert.NotEqual(ModelCallOutcome.ConfigError, response.Status);
    }

    /// <summary>
    /// A confined agent runs INSIDE the workspace. The agent is asked where it is and its own answer
    /// is the assertion — the one fact no amount of reading the code establishes.
    /// </summary>
    [Fact]
    public void AConfinedAgent_RunsInsideTheWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var agent = Shell("confined", Pwd(), writes: true);

            var response = new AgentCliProvider(agent, TimeSpan.FromSeconds(20), dir)
                .Send(ModelRequest.FromPrompt("x"));

            Assert.True(response.Ok, $"the agent did not run: {response.Content}");
            // EndsWith rather than Equals: the leaf is unique, and on hosts where the temp root is a
            // symlink the shell reports the resolved path, which is the same directory by another
            // name and would fail an equality check for no reason anyone cares about.
            Assert.EndsWith(Path.GetFileName(dir), response.Content.Trim(), StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>
    /// A workspace that does not exist is its own refusal, naming the directory.
    ///
    /// Process.Start with a missing working directory throws the same Win32Exception as a missing
    /// binary, and this provider maps that to "the agent is not installed" — an error naming the
    /// wrong problem and prescribing an install that would not fix it.
    /// </summary>
    [Fact]
    public void AWorkspaceThatDoesNotExist_IsRefusedByName_NotReportedAsNotInstalled()
    {
        var missing = Path.Combine(Path.GetTempPath(), "anthill-absent-" + Guid.NewGuid().ToString("N"));
        var agent = Shell("confined-nowhere", Echo("hi"), writes: true);

        var response = new AgentCliProvider(agent, TimeSpan.FromSeconds(20), missing)
            .Send(ModelRequest.FromPrompt("x"));

        // ConfigError specifically, not NotAvailable: the distinction IS the fix.
        Assert.Equal(ModelCallOutcome.ConfigError, response.Status);
        Assert.Contains(missing, response.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE REGRESSION TEST. The factory must pass the workspace through, and this is the assertion
    /// that would have failed before the fix.
    ///
    /// Observed through behaviour rather than by reading a private field: given a workspace that
    /// does not exist, a provider the factory built must refuse BY THAT NAME. A factory that drops
    /// the workspace produces a provider that knows nothing about it and could not name it. That the
    /// real agent binary is probably not installed does not matter — confinement is checked before
    /// anything is started, which is the point.
    /// </summary>
    [Fact]
    public void TheFactory_PassesTheWorkspaceToTheProvider()
    {
        var claimed = Path.Combine(Path.GetTempPath(), "anthill-factory-" + Guid.NewGuid().ToString("N"));
        var agent = AgentCliCatalog.All.First(a => a.Writes);
        var ctx = new ReasoningProviderContext(
            agent.Id, agent.DisplayName, null, "", new StubOptions(claimed));

        var response = new AgentCliProviderFactory().Create(ctx).Send(ModelRequest.FromPrompt("x"));

        Assert.Equal(ModelCallOutcome.ConfigError, response.Status);
        Assert.Contains(claimed, response.Content, StringComparison.Ordinal);
    }

    /// <summary>And with no workspace configured, the factory's provider refuses rather than roams.</summary>
    [Fact]
    public void TheFactory_WithNoWorkspaceConfigured_ProducesAProviderThatRefuses()
    {
        var agent = AgentCliCatalog.All.First(a => a.Writes);
        var ctx = new ReasoningProviderContext(
            agent.Id, agent.DisplayName, null, "", new StubOptions(null));

        var response = new AgentCliProviderFactory().Create(ctx).Send(ModelRequest.FromPrompt("x"));

        Assert.Equal(ModelCallOutcome.ConfigError, response.Status);
        Assert.Contains("workspace", response.Content, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubOptions : IReasoningRuntimeOptions
    {
        private readonly string? _root;
        public StubOptions(string? root) => _root = root;
        public int ModelCallTimeoutSeconds => 5;
        public string? AgentWorkspaceRoot => _root;
    }

    /// <summary>A catalogue entry backed by the platform shell, so these tests need no vendor tool.</summary>
    private static AgentCli Shell(string id, IReadOnlyList<string> args, bool writes) => new()
    {
        Id = AgentCliCatalog.IdPrefix + id,
        DisplayName = id,
        Vendor = "test",
        Binary = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
        PromptArgs = args,
        PackageManager = "npm",
        Package = "n/a",
        AuthCommand = "n/a",
        DocsUrl = "https://example.invalid",
        Writes = writes,
    };

    private static IReadOnlyList<string> Pwd() =>
        OperatingSystem.IsWindows() ? new[] { "/c", "cd" } : new[] { "-c", "pwd" };

    private static IReadOnlyList<string> Echo(string what) =>
        OperatingSystem.IsWindows() ? new[] { "/c", "echo " + what } : new[] { "-c", "echo " + what };

    // Quoted so a path containing a space stays one word to the shell. These are the ONE deliberate
    // exception to "operator text never reaches a shell": the string is built here, in the test.
    private static IReadOnlyList<string> Touch(string path) =>
        OperatingSystem.IsWindows()
            ? new[] { "/c", $"type nul > \"{path}\"" }
            : new[] { "-c", $"touch '{path}'" };

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

    // ---- v0.3.8.52: the Windows path — .cmd shims, cmd.exe, and the injection rule -------------
    //
    // On Windows npm and every npm-installed agent is a .cmd, which CreateProcess cannot start:
    // the field report's "all installers must work" was every probe, install and run failing at
    // Process.Start. The repair has two lanes — npm shims rewritten to node + discrete argv, and
    // everything else through cmd.exe only with arguments cmd cannot interpret. Each lane's
    // decision logic is pure, so it is pinned here on every OS: the platform this suite mostly
    // runs on is NOT the platform the defect shipped on.

    /// <summary>The cmd lane refuses what it cannot make safe: escaping cmd is a losing game
    /// (%expansion% survives quoting), and the arguments that legitimately ride this lane are
    /// repository constants — so anything cmd would interpret is refused, never escaped.</summary>
    [Theory]
    [InlineData("install", true)]
    [InlineData("--prefix", true)]
    [InlineData(@"C:\Users\Mína O'Brien\.anthill\agents", true)]  // spaces, accents, apostrophes: normal homes
    [InlineData("@anthropic-ai/claude-code", true)]
    [InlineData("%PATH%", false)]                                  // cmd expands inside quotes
    [InlineData("a\"b", false)]                                    // escapes the quoting
    [InlineData("x & del *", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("pipe|through", false)]
    [InlineData("caret^", false)]
    public void CmdLane_RefusesWhatCmdWouldInterpret(string arg, bool safe) =>
        Assert.Equal(safe, AgentCliDiscovery.ShellSafeArg(arg));

    /// <summary>
    /// The npm-shim parser recognises the real cmd-shim shape — `"%_prog%" "%dp0%\<target>.js" %*`
    /// and the older `%~dp0` spelling — and nothing else. The fixture text is the PRODUCER's own
    /// output format (cmd-shim's, structure verbatim), per the cross-boundary rule: a hand-invented
    /// shim format would test agreement with an assumption.
    /// </summary>
    [Fact]
    public void NpmShimParser_ExtractsTheNodeTarget_FromBothShimSpellings()
    {
        // cmd-shim ≥5 (what current npm writes): SET dp0=%~dp0, then the target via "%dp0%\...".
        const string modern = "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n"
            + ":start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\r\nIF EXIST \"%dp0%\\node.exe\" (\r\n"
            + "  SET \"_prog=%dp0%\\node.exe\"\r\n) ELSE (\r\n  SET \"_prog=node\"\r\n)\r\n\r\n"
            + "endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "
            + "\"%_prog%\"  \"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n";
        Assert.Equal(@"node_modules\@anthropic-ai\claude-code\cli.js",
            AgentCliDiscovery.NpmShimRelativeTarget(modern));

        // The older direct spelling.
        const string older = "@\"node\"  \"%~dp0\\node_modules\\opencode-ai\\bin\\opencode.js\" %*\r\n";
        Assert.Equal(@"node_modules\opencode-ai\bin\opencode.js",
            AgentCliDiscovery.NpmShimRelativeTarget(older));

        // A .cmd an operator wrote is NOT a shim and must not be misread as one.
        Assert.Null(AgentCliDiscovery.NpmShimRelativeTarget("@echo off\r\nformat C: /y\r\n"));
        Assert.Null(AgentCliDiscovery.NpmShimRelativeTarget(""));
    }

    /// <summary>Off Windows the invocation is exactly what it always was — the resolved binary and
    /// an untouched argv. The Windows translation must be unreachable dead weight everywhere else.</summary>
    [Fact]
    public void OffWindows_TheInvocationIsUntouched()
    {
        if (OperatingSystem.IsWindows()) return; // the pure lane tests above cover the logic

        var inv = AgentCliDiscovery.BuildInvocation("definitely-not-installed-xyz", new[] { "-p", "a % b & c" });
        Assert.Equal("definitely-not-installed-xyz", inv.FileName);
        Assert.Equal(new[] { "-p", "a % b & c" }, inv.Args);
        Assert.Null(inv.RawCmdLine);
    }

    /// <summary>The remedy names THIS machine's tools. `sudo apt install nodejs npm` reached a
    /// Windows operator in the field; the hints are platform-forked now, and this pins the fork
    /// actually reaching the strings an operator is shown.</summary>
    [Fact]
    public void InstallGuidance_SpeaksThisPlatformsLanguage()
    {
        var pipHint = AgentCliCatalog.InstallHint(AgentCliCatalog.All.First(a => a.PackageManager == "pip"));
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("python ", pipHint);
            Assert.DoesNotContain("sudo", pipHint);
        }
        else
        {
            Assert.StartsWith("python3 ", pipHint);
        }
    }
}
