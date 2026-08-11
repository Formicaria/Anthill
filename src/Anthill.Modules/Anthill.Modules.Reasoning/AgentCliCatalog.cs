namespace Anthill.Modules.Reasoning;

/// <summary>
/// One installable command-line agent the colony can delegate to. v3.8.39.
///
/// These are the shipped, supported CLI forms of the commercial agents — Claude Code, Codex,
/// Gemini CLI, and the open ones beside them. They are not a workaround for an API: they are how
/// those vendors intend their agents to be driven from another program, which is why each one has
/// its own login command and keeps its own credentials.
///
/// THE CREDENTIAL RULE, and the reason this shape was chosen over any other:
///
/// Anthill never sees, stores, forwards or replays a vendor credential. The operator runs the
/// tool's own <see cref="AuthCommand"/> once, exactly as they would if Anthill did not exist, and
/// the tool holds its own session from then on. Anthill only ever starts a process. That is not a
/// limitation accepted for policy reasons — it is strictly stronger than a credential vault would
/// be: there is no secret in Anthill's database to leak, no token to refresh, and nothing to
/// re-authenticate when a vendor rotates its auth. An operator revokes access in the vendor's own
/// account settings and it is revoked here too, with no Anthill involvement at all.
///
/// Purely declarative, and deliberately so. Nothing here runs a process, touches PATH or reads a
/// filesystem — <see cref="AgentCliDiscovery"/> does that. A catalogue that probed on construction
/// could not be tested without installing five vendors' tools.
/// </summary>
public sealed record AgentCli
{
    /// <summary>Stable id. This is the provider id the router routes to, so it is a wire value.</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Who ships it. Shown so an operator knows whose account they are about to use.</summary>
    public required string Vendor { get; init; }

    /// <summary>The executable name, resolved against PATH. No path separators — see AgentCliDiscovery.</summary>
    public required string Binary { get; init; }

    /// <summary>Arguments that make it print a version and exit. The cheapest proof it is really there.</summary>
    public IReadOnlyList<string> VersionArgs { get; init; } = new[] { "--version" };

    /// <summary>
    /// Arguments for one non-interactive turn, with <c>{prompt}</c> replaced by the operator's text.
    ///
    /// Every agent here supports a headless single-shot mode. An agent that only ran interactively
    /// could not be a reasoning provider, because there would be nobody at the terminal to answer it.
    /// </summary>
    public required IReadOnlyList<string> PromptArgs { get; init; }

    /// <summary>How the operator installs it, verbatim, to run in their own shell.</summary>
    public required string InstallCommand { get; init; }

    /// <summary>
    /// How the operator authenticates it — in the vendor's own flow, in their own terminal.
    /// Anthill prints this; it never runs it, because a login is an interactive act belonging to
    /// the person whose account it is.
    /// </summary>
    public required string AuthCommand { get; init; }

    public required string DocsUrl { get; init; }

    /// <summary>
    /// True when the agent edits files and runs commands on its own, rather than only answering.
    ///
    /// Load-bearing rather than descriptive: an agent that writes must be confined to a mission
    /// workspace like any other actor, and the colony's rule that the active checkout is never a
    /// scratchpad does not stop applying because the writer came from another vendor.
    /// </summary>
    public bool Writes { get; init; }
}

/// <summary>
/// The agents Anthill knows how to install, authenticate and delegate to.
///
/// Adding one is a data change, not a code change — which is the point. The list will keep moving
/// as vendors ship and rename their tools, and a catalogue that required a new class per agent
/// would guarantee it falls behind.
/// </summary>
public static class AgentCliCatalog
{
    /// <summary>Provider ids here are namespaced so they can never collide with a model name.</summary>
    public const string IdPrefix = "agent:";

    private static readonly AgentCli[] Known =
    {
        new()
        {
            Id = IdPrefix + "claude-code",
            DisplayName = "Claude Code",
            Vendor = "Anthropic",
            Binary = "claude",
            PromptArgs = new[] { "-p", "{prompt}" },
            InstallCommand = "npm install -g @anthropic-ai/claude-code",
            AuthCommand = "claude",           // first run walks the operator through sign-in
            DocsUrl = "https://docs.claude.com/en/docs/claude-code/overview",
            Writes = true,
        },
        new()
        {
            Id = IdPrefix + "codex",
            DisplayName = "Codex CLI",
            Vendor = "OpenAI",
            Binary = "codex",
            PromptArgs = new[] { "exec", "{prompt}" },
            InstallCommand = "npm install -g @openai/codex",
            AuthCommand = "codex login",
            DocsUrl = "https://developers.openai.com/codex/cli",
            Writes = true,
        },
        new()
        {
            Id = IdPrefix + "gemini",
            DisplayName = "Gemini CLI",
            Vendor = "Google",
            Binary = "gemini",
            PromptArgs = new[] { "-p", "{prompt}" },
            InstallCommand = "npm install -g @google/gemini-cli",
            AuthCommand = "gemini",
            DocsUrl = "https://github.com/google-gemini/gemini-cli",
            Writes = true,
        },
        new()
        {
            Id = IdPrefix + "aider",
            DisplayName = "Aider",
            Vendor = "Aider (open source)",
            Binary = "aider",
            PromptArgs = new[] { "--no-pretty", "--yes", "--message", "{prompt}" },
            InstallCommand = "python -m pip install aider-install && aider-install",
            AuthCommand = "aider --model <model>   # uses your own provider API key",
            DocsUrl = "https://aider.chat/docs/install.html",
            Writes = true,
        },
        new()
        {
            Id = IdPrefix + "opencode",
            DisplayName = "OpenCode",
            Vendor = "OpenCode (open source)",
            Binary = "opencode",
            PromptArgs = new[] { "run", "{prompt}" },
            InstallCommand = "npm install -g opencode-ai",
            AuthCommand = "opencode auth login",
            DocsUrl = "https://opencode.ai/docs/",
            Writes = true,
        },
    };

    public static IReadOnlyList<AgentCli> All => Known;

    public static AgentCli? ById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Known.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an id names an agent at all. Asked by the factory before it builds one.</summary>
    public static bool IsAgentId(string? id) =>
        id is not null && id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The prompt argument vector, with the placeholder filled.
    ///
    /// Returned as a LIST, never as a joined string, and this is the security-relevant decision in
    /// the file. The prompt is operator text and may contain quotes, newlines, semicolons or
    /// backticks; handed to a shell it would be a command-injection vector. Passed as discrete
    /// argv entries to a process started without a shell, it cannot be anything but an argument.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(AgentCli agent, string prompt) =>
        agent.PromptArgs.Select(a => a.Replace("{prompt}", prompt, StringComparison.Ordinal)).ToList();
}
