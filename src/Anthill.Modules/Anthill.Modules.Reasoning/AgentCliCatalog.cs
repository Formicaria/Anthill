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

    /// <summary>
    /// v0.3.8.47: arguments for a turn whose stdout STREAMS as NDJSON events, for agents that
    /// have such a mode. Null means the agent has no streaming mode and plain line streaming is
    /// the honest best; Claude Code buffers a piped answer entirely, which is why this exists.
    /// </summary>
    public IReadOnlyList<string>? StreamArgs { get; init; }

    /// <summary>
    /// Which package manager ships it: "npm", "pip", or "" for an agent Anthill cannot install.
    ///
    /// v0.3.8.41 — split out of the old free-text InstallCommand. A verbatim shell line could only
    /// be run as written, and as written every npm one was `install -g`, which writes to
    /// /usr/lib/node_modules and fails with EACCES for any non-root operator. The manager and the
    /// package name are now separate so the installer can decide the DESTINATION, which is the
    /// whole fix.
    /// </summary>
    public required string PackageManager { get; init; }

    /// <summary>The package to install. Never interpolated into a shell by the installer.</summary>
    public required string Package { get; init; }

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

    // ---- v0.3.8.51: the operator's approval gate, translated into THIS agent's own flags -------
    //
    // Field report: a headless agent run has nobody at the terminal, so its own permission prompts
    // are unanswerable and every mutating call dies. The operator already answered once — in chat,
    // at the approval selector — and these per-agent arg fragments are how that answer reaches the
    // spawned process. Null/empty means the agent has no such flag and gets NOTHING extra: an
    // unmapped agent stays exactly as locked down as before, which is the honest default.

    /// <summary>Flags that let the agent EDIT within its confined workspace without prompting.
    /// Applied for every approved mission — the workspace is a disposable sandbox, and the real
    /// tree still only changes through Anthill's own patch-approval pipeline.</summary>
    public IReadOnlyList<string>? AcceptEditsArgs { get; init; }

    /// <summary>Flags granting a BOUNDED tool set (build/test commands) under Automatically
    /// approve. Never network-wide, never arbitrary shell.</summary>
    public IReadOnlyList<string>? AutoApproveToolArgs { get; init; }

    /// <summary>Flags that skip the agent's own prompts entirely — the mapping of "Skip all
    /// approvals", which the operator confirms in spoken words before it can be set.</summary>
    public IReadOnlyList<string>? BypassArgs { get; init; }

    /// <summary>Per-directory reach, with <c>{dir}</c> replaced by one granted absolute path.
    /// Repeated for each directory gate the operator opened.</summary>
    public IReadOnlyList<string>? AddDirArgs { get; init; }

    /// <summary>
    /// v0.3.8.51, second field round: where this agent reads PROJECT-LEVEL permissions from,
    /// relative to its working directory. The colony's own probe found the real wall — with no
    /// project settings file, a headless run falls through to harness defaults that flags do not
    /// fully override. Anthill materializes this file from the operator's policy before each run
    /// (see <see cref="AgentCliCatalog.MaterializeLocalSettings"/>). Null = the agent has no such
    /// mechanism and gets flags only.
    /// </summary>
    public string? LocalSettingsRelativePath { get; init; }
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
            // stream-json emits one JSON event per line as the answer is produced; --verbose is
            // required by the CLI for stream-json in print mode.
            // --include-partial-messages adds stream_event token deltas; without it stream-json
            // emits one assistant event per COMPLETE message and nothing trickles.
            StreamArgs = new[] { "-p", "{prompt}", "--output-format", "stream-json", "--verbose", "--include-partial-messages" },
            PackageManager = "npm",
            Package = "@anthropic-ai/claude-code",
            AuthCommand = "claude",           // first run walks the operator through sign-in
            DocsUrl = "https://docs.claude.com/en/docs/claude-code/overview",
            Writes = true,
            // v0.3.8.51: the approval gate's translation for Claude Code specifically. acceptEdits
            // lets it edit inside its confined workspace; the bounded tool list covers the build
            // and test commands a coding mission needs and nothing network-shaped; bypass maps
            // Skip-all-approvals onto the CLI's own equivalent; add-dir is a directory gate.
            AcceptEditsArgs = new[] { "--permission-mode", "acceptEdits" },
            AutoApproveToolArgs = new[] { "--allowedTools", "Edit,Write,Bash(dotnet:*),Bash(node:*),Bash(npm:*),Bash(git status:*),Bash(git diff:*),Bash(git log:*)" },
            BypassArgs = new[] { "--dangerously-skip-permissions" },
            AddDirArgs = new[] { "--add-dir", "{dir}" },
            // settings.local.json is Claude Code's own not-checked-in local layer — the right
            // file for machine-materialized permissions that must never reach the operator's VCS.
            LocalSettingsRelativePath = ".claude/settings.local.json",
        },
        new()
        {
            Id = IdPrefix + "codex",
            DisplayName = "Codex CLI",
            Vendor = "OpenAI",
            Binary = "codex",
            PromptArgs = new[] { "exec", "{prompt}" },
            PackageManager = "npm",
            Package = "@openai/codex",
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
            PackageManager = "npm",
            Package = "@google/gemini-cli",
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
            PackageManager = "pip",
            Package = "aider-chat",
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
            PackageManager = "npm",
            Package = "opencode-ai",
            AuthCommand = "opencode auth login",
            DocsUrl = "https://opencode.ai/docs/",
            Writes = true,
        },
    };

    public static IReadOnlyList<AgentCli> All => Known;

    /// <summary>
    /// What the operator would type to install it themselves, for display and for a refusal that
    /// names the remedy. NOT what Anthill runs — the installer targets its own directory instead,
    /// so the two differ deliberately and this one is the copy-pasteable version.
    /// </summary>
    public static string InstallHint(AgentCli agent) => agent.PackageManager switch
    {
        "npm" => $"npm install -g {agent.Package}",
        "pip" => $"python3 -m pip install --user {agent.Package}",
        _ => $"see {agent.DocsUrl}",
    };

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

    /// <summary>v0.3.8.47: the streaming variant. Falls back to the plain args when the agent has none.</summary>
    public static IReadOnlyList<string> BuildStreamArgs(AgentCli agent, string prompt) =>
        (agent.StreamArgs ?? agent.PromptArgs)
        .Select(a => a.Replace("{prompt}", prompt, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// v0.3.8.51 — the operator's approval gate, as THIS agent's flags. Pure function of the
    /// ambient access context so it is testable without a process:
    ///
    /// <list type="bullet">
    /// <item>ask — the mission itself was operator-approved, so the agent may EDIT inside its
    ///   confined disposable workspace (<see cref="AgentCli.AcceptEditsArgs"/>); its own command
    ///   prompts stay in force, honestly refused rather than fake-approved.</item>
    /// <item>autoapprove — edits plus the BOUNDED tool set (build/test commands).</item>
    /// <item>bypass — the agent's own skip flag; the operator confirmed this in words.</item>
    /// <item>every granted directory — one <see cref="AgentCli.AddDirArgs"/> expansion each.</item>
    /// </list>
    ///
    /// A null context grants nothing. An agent without mapped flags gets nothing. Absence is not
    /// consent, in either direction.
    /// </summary>
    public static IReadOnlyList<string> BuildAccessArgs(AgentCli agent, Anthill.SDK.Reasoning.AgentAccessScope.Context? access)
    {
        if (access is null) return Array.Empty<string>();
        var args = new List<string>();

        switch (access.PolicyWire)
        {
            case "bypass" when agent.BypassArgs is { Count: > 0 }:
                args.AddRange(agent.BypassArgs);
                break;
            case "autoapprove":
                if (agent.AcceptEditsArgs is { Count: > 0 }) args.AddRange(agent.AcceptEditsArgs);
                if (agent.AutoApproveToolArgs is { Count: > 0 }) args.AddRange(agent.AutoApproveToolArgs);
                break;
            case "ask" when access.ConfinedWorkspace:
                // In a DISPOSABLE mission sandbox the approved mission is the approved work; in a
                // live directory (the chat lane) Manual approval grants nothing — per-edit prompts
                // are unanswerable headless, so the honest move is to propose a mission instead.
                if (agent.AcceptEditsArgs is { Count: > 0 }) args.AddRange(agent.AcceptEditsArgs);
                break;
        }

        if (agent.AddDirArgs is { Count: > 0 })
            foreach (var dir in access.GrantedDirectories.Where(d => !string.IsNullOrWhiteSpace(d)))
                args.AddRange(agent.AddDirArgs.Select(a => a.Replace("{dir}", dir, StringComparison.Ordinal)));

        return args;
    }

    /// <summary>A marker key so Anthill can tell ITS materialized settings from an operator's own
    /// file. Anthill only ever overwrites or deletes a file carrying this key.</summary>
    public const string SettingsMarkerKey = "_anthill";

    /// <summary>
    /// v0.3.8.51, second field round — the permission payload the colony's own probe identified as
    /// the real unblock: "The repo has no .claude/ directory — everything falls through to harness
    /// defaults." Pure function of the access context; null means NOTHING is granted and any
    /// previously materialized file must be REMOVED (a downgrade to Manual approval has to close
    /// the gate it once opened).
    ///
    /// The allow list mirrors <see cref="BuildAccessArgs"/> exactly — flags and settings are one
    /// policy expressed through two channels, because the field showed one channel is not enough.
    /// </summary>
    public static string? BuildLocalSettingsJson(Anthill.SDK.Reasoning.AgentAccessScope.Context? access)
    {
        if (access is null) return null;

        var allow = access.PolicyWire switch
        {
            "bypass" or "autoapprove" => new List<string>
            {
                "Edit", "Write",
                "Bash(dotnet:*)", "Bash(node:*)", "Bash(npm:*)",
                "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)",
                "Bash(ls:*)", "Bash(cat:*)", "Bash(grep:*)", "Bash(find:*)",
            },
            "ask" when access.ConfinedWorkspace => new List<string> { "Edit", "Write" },
            _ => new List<string>(),
        };
        if (allow.Count == 0 && access.GrantedDirectories.Count == 0) return null;

        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [SettingsMarkerKey] = "materialized by Anthill from the conversation's approval policy — "
                                + "regenerated per run, safe to delete",
            ["permissions"] = new Dictionary<string, object?>
            {
                ["allow"] = allow,
                ["additionalDirectories"] = access.GrantedDirectories,
            },
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Write (or remove) the agent's project-level permission file in <paramref name="workingDirectory"/>
    /// to match the operator's current policy. Three rules, all fail-safe:
    ///
    /// <list type="bullet">
    /// <item>An existing file WITHOUT Anthill's marker is the operator's own — never touched.</item>
    /// <item>A policy granting nothing deletes a marker-carrying file: downgrades close gates.</item>
    /// <item>Any IO failure degrades toward LESS access and never fails the run — the agent then
    ///   refuses honestly, which is the pre-existing behaviour.</item>
    /// </list>
    /// </summary>
    public static void MaterializeLocalSettings(AgentCli agent, string? workingDirectory,
        Anthill.SDK.Reasoning.AgentAccessScope.Context? access)
    {
        if (agent.LocalSettingsRelativePath is null || string.IsNullOrWhiteSpace(workingDirectory)) return;
        try
        {
            if (!Directory.Exists(workingDirectory)) return;
            var path = Path.Combine(workingDirectory, agent.LocalSettingsRelativePath);

            if (File.Exists(path)
                && !File.ReadAllText(path).Contains($"\"{SettingsMarkerKey}\"", StringComparison.Ordinal))
                return;   // the operator's own settings — theirs, not ours

            var json = BuildLocalSettingsJson(access);
            if (json is null)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[agent-cli] could not materialize {agent.LocalSettingsRelativePath} "
                + $"in {workingDirectory}: {error.Message}");
        }
    }
}
