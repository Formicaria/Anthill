using System.Text.RegularExpressions;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// V&amp;V — "no call site, no feature", enforced rather than remembered.
///
/// This exists because of a real failure, and the failure is worth recording exactly. v3.7.0 shipped
/// with all five exit gates met, a version bump, a release tag and a push — and its entire runtime
/// was unreachable. <c>ConversationRunner</c> was never constructed outside tests, and
/// <c>ConversationScope.Enter</c> was called only from tests, which meant the escalation gate wired
/// into <c>ToolRegistry.RunTool</c> evaluated to null on every production path and silently passed.
///
/// Every gate was true of the code. None was true of the running system. Unit tests cannot catch
/// that — they ARE the thing providing the false call site — so the check has to be structural.
///
/// These are deliberately crude source scans. A precise version would need a call graph; a crude one
/// that fails loudly when a subsystem has no production entry point catches the whole class of
/// mistake, which is the one that keeps happening.
/// </summary>
public class CallSiteAuditTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Anthill.Core")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root");
        return dir!.FullName;
    }

    /// <summary>Every production .cs file, excluding build output.</summary>
    private static IReadOnlyList<string> ProductionSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

    private static string ProductionText(params string[] excludingFileNames)
    {
        var text = new System.Text.StringBuilder();
        foreach (var file in ProductionSources())
        {
            if (excludingFileNames.Any(n => Path.GetFileName(file).Equals(n, StringComparison.OrdinalIgnoreCase)))
                continue;
            text.Append(File.ReadAllText(file)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// The exact regression. A conversation runtime nobody constructs is a policy engine with no
    /// enforcement — and its gate returns null rather than refusing, so the failure is silent.
    /// </summary>
    [Fact]
    public void TheConversationRuntime_IsConstructedInProduction()
    {
        var production = ProductionText("ConversationRunner.cs");

        // Matched on the TYPE NAME preceded by `new`, allowing a namespace qualifier: the
        // composition root writes `new Anthill.Core.Conversations.ConversationRunner(...)`, and an
        // assertion on the bare literal fails on a perfectly correct call site. A guard that is
        // right about the requirement and wrong about the spelling teaches people to delete guards.
        Assert.Matches(@"new\s+([A-Za-z.]+\.)?ConversationRunner\s*\(", production);
    }

    /// <summary>
    /// And something must ENTER a scope. The gate in RunTool asks ConversationScope.Evaluate, which
    /// answers null outside a scope — so with no production Enter, every escalation check passes and
    /// the mechanism is decorative.
    /// </summary>
    [Fact]
    public void SomethingInProduction_EntersAConversationScope()
    {
        var production = ProductionText("ConversationScope.cs");

        Assert.Contains("ConversationScope.Enter", production);
    }

    /// <summary>
    /// The mission workspace scope has the same shape and the same failure mode: writes are confined
    /// only while a scope is entered, so an unentered scope silently returns to the old behaviour of
    /// writing into the live checkout.
    /// </summary>
    [Fact]
    public void SomethingInProduction_EntersAMissionWorkspaceScope()
    {
        var production = ProductionText("MissionWorkspaceScope.cs");

        Assert.Contains("MissionWorkspaceScope.Enter", production);
    }

    /// <summary>
    /// v3.8.0 — the durable attempt layer must be REACHED by the dispatcher, not merely available.
    ///
    /// The same guard as the conversation runtime above, applied to the phase most likely to repeat
    /// that mistake: schema, records and an atomic claim can all exist, be thoroughly tested, and
    /// never once run during a mission. Every exit gate in this phase is about what survives a crash,
    /// and not one of them is true of a claim nobody takes.
    ///
    /// SqliteMemory is excluded from each scan because it DEFINES these operations — counting a
    /// method's own declaration as its call site is exactly how a dead subsystem passes an audit.
    /// </summary>
    [Fact]
    public void TheDispatcher_TakesADurableClaim() =>
        Assert.Contains("TryClaimTask", ProductionText("SqliteMemory.Attempts.cs"));

    /// <summary>
    /// And something must CLOSE what it opened. A claim taken and never finished holds a lease
    /// against a task that has already ended, so every retry is refused until the lease lapses —
    /// the durable layer would make the colony worse rather than better.
    /// </summary>
    [Fact]
    public void SomethingInProduction_FinishesTheAttemptsItStarts() =>
        Assert.Contains("FinishAttempt", ProductionText("SqliteMemory.Attempts.cs"));

    /// <summary>
    /// Startup must reconcile. "No accepted task is silently lost after crash or restart" is a
    /// promise about the NEXT process, kept only if that process sweeps for what the last one left
    /// holding — an expired lease nobody looks for is just a stuck task.
    /// </summary>
    [Fact]
    public void Startup_ReclaimsWorkAbandonedByADeadProcess() =>
        Assert.Contains("ReclaimExpiredAttempts", ProductionText("SqliteMemory.Attempts.cs"));

    /// <summary>
    /// And it must reclaim its OWN orphans, which the expiry sweep cannot see: a process killed
    /// mid-task leaves its attempts Running with most of the lease still on the clock, so the sweep
    /// finds nothing at restart and the task stays stranded until that lease runs out.
    /// </summary>
    [Fact]
    public void Startup_ReclaimsTheOrphansThisWorkerLeftBehind() =>
        Assert.Contains("ReclaimOwnAttempts", ProductionText("SqliteMemory.Attempts.cs"));

    /// <summary>
    /// And a worker must actually register. A lease is only meaningful against an identity that can
    /// stop reporting; an attempt held by a worker nobody ever registered cannot be reasoned about
    /// after a crash.
    /// </summary>
    [Fact]
    public void ThisProcess_RegistersItselfAsAWorker() =>
        Assert.Contains("LocalWorker.Register", ProductionText("LocalWorker.cs"));

    /// <summary>
    /// And it must keep reporting. Registration reports alive exactly once, so without a repeating
    /// heartbeat the worker goes stale within minutes and reads as crashed while it sits there
    /// working — which inverts the availability rule rather than merely weakening it: a healthy
    /// colony would look dead, and a genuinely dead one would look identical.
    /// </summary>
    [Fact]
    public void TheWorker_KeepsReportingItselfAlive() =>
        Assert.Contains("Memory.Heartbeat", ProductionText("SqliteMemory.Attempts.cs"));

    /// <summary>
    /// Every tool the inventory claims exists must be constructed by the composition root.
    ///
    /// A name in the inventory with no registration is a tool a role is AUTHORIZED to call and that
    /// will never be found — reported at runtime as "not registered", which reads as a config
    /// problem rather than a missing feature.
    ///
    /// The mapping is an explicit table rather than a naming convention, because no convention
    /// holds: list_directory is DirectoryListTool and read_changed_files_summary is
    /// ChangedFilesSummaryTool. A convention-based check would have silently passed on both, which
    /// is how a guard becomes decoration.
    /// </summary>
    [Fact]
    public void EveryImplementedTool_IsRegisteredByTheCompositionRoot()
    {
        // v3.8.16 — there are now TWO places a tool can be constructed, and the guard has to read
        // both or it fails on the six that moved. Queen.BuildToolRegistry still composes the tools
        // that stayed; ToolsModule.Register composes the ones that left.
        //
        // Reading both is not a weakening. The property under test is "every name the inventory
        // claims is actually constructed somewhere the colony composes", and a tool constructed in
        // NEITHER file still fails. What would weaken it is scanning the whole tree, because then
        // any `new ShellCommandTool(` in a test would satisfy it — which is why these are two named
        // files rather than a glob.
        var roots = new[]
        {
            Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs"),
            Path.Combine(RepoRoot(), "src", "Anthill.Modules", "Anthill.Modules.Tools", "ToolsModule.cs"),
        };

        foreach (var root in roots)
            Assert.True(File.Exists(root), $"A composition root this guard reads is missing: {root}");

        var source = string.Concat(roots.Select(File.ReadAllText));
        var constructed = Regex.Matches(source, @"new\s+([A-Za-z]+Tool)\s*\(")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var implementedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["system_info"] = "SystemInfoTool",
            ["run_allowlisted_check"] = "RunAllowlistedCheckTool",
            ["list_directory"] = "DirectoryListTool",
            ["read_text_file"] = "ReadTextFileTool",
            ["write_text_file"] = "WriteTextFileTool",
            ["web_search"] = "WebSearchTool",
            ["shell_command"] = "ShellCommandTool",
            ["apply_patch"] = "ApplyPatchTool",
            ["search_workspace"] = "SearchWorkspaceTool",
            ["read_changed_files_summary"] = "ChangedFilesSummaryTool",
            ["repository_index"] = "RepositoryIndexTool",
        };

        // A new tool must be added HERE as well as to the inventory. That is deliberate friction:
        // the pairing is the thing being checked, so it cannot be derived from either side alone.
        var unmapped = ToolInventory.Implemented.Where(n => !implementedBy.ContainsKey(n)).ToList();
        Assert.True(unmapped.Count == 0,
            "These tools are in ToolInventory.Implemented but this audit does not know which type "
          + "implements them — add them to implementedBy so the registration can be checked: "
          + string.Join(", ", unmapped));

        var missing = ToolInventory.Implemented
            .Where(n => !constructed.Contains(implementedBy[n]))
            .ToList();

        Assert.True(missing.Count == 0,
            "These tools are declared implemented but constructed by neither Queen.BuildToolRegistry "
          + "nor ToolsModule.Register, so a role allowed them would be told the tool does not "
          + "exist: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Both composition roots must load the tools module AND drain what it contributed.
    ///
    /// v3.8.16 — the exact regression this repository keeps producing, caught before it shipped
    /// rather than after. `Anthill.Cli` has loaded modules since v3.8.6 and never once read
    /// <c>ContributedTools</c>: harmless for ten releases because no module shipped a tool, and the
    /// moment one did, `anthill --mission` would have lost list_directory, read_text_file,
    /// write_text_file, web_search, shell_command and apply_patch.
    ///
    /// It would have lost them SILENTLY. An unregistered tool returns a typed ValidationFailure, so
    /// the mission completes, reports success, and simply does less — which reads as a weak model
    /// rather than as a missing capability. That is the same failure shape as the specialist
    /// contracts naming tools nobody built, which is why <c>ToolInventory</c> exists.
    ///
    /// Loading without draining is the interesting half: it compiles, it boots, and every guard that
    /// only checks the module is composed would pass.
    /// </summary>
    [Theory]
    [InlineData("src/Anthill.Api/ApiHost.cs")]
    [InlineData("src/Anthill.Cli/Program.cs")]
    public void EveryCompositionRoot_LoadsTheToolsModule_AndDrainsIt(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("ToolsModule(", source);
        Assert.Contains("AdoptModuleTools(", source);
        Assert.Contains("ContributedTools", source);
    }

    /// <summary>
    /// Every persisted table must be both written and read somewhere in production. A table written
    /// and never read is data an operator was promised and cannot see; one read and never written
    /// answers every question with "nothing".
    /// </summary>
    [Fact]
    public void EveryTable_IsBothWrittenAndRead()
    {
        var schema = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Memory", "SqliteMemory.Schema.cs"));
        var production = ProductionText();

        var tables = Regex.Matches(schema, @"CREATE TABLE IF NOT EXISTS ([a-z_]+)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();

        // The one KNOWN write-only table, named rather than silently tolerated.
        //
        // task_result_summaries predates the v3.2.0 structured ant result (task_results) and was
        // superseded by it: every task still writes a summary row that nothing reads. It is listed
        // here because inventing a reader to satisfy this guard would be backwards — the guard
        // exists to surface the fact, and the fact is that this table should be retired.
        var knownWriteOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "task_result_summaries",
        };

        var problems = new List<string>();
        foreach (var table in tables)
        {
            if (knownWriteOnly.Contains(table)) continue;
            var written = Regex.IsMatch(production, $@"(INSERT (OR (REPLACE|IGNORE) )?INTO|UPDATE|DELETE FROM)\s+{table}\b");
            // JOIN counts as a read — patch_sets is only ever reached through a LEFT JOIN, and a
            // FROM-only check reports it as dead when it is not.
            var read = Regex.IsMatch(production, $@"(FROM|JOIN)\s+{table}\b");

            if (!written) problems.Add($"{table} is never written");
            if (!read) problems.Add($"{table} is never read");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// v0.3.8.55 — every redirected pipe is decoded as UTF-8, structurally. The Windows field
    /// report: agent CLI answers showed "â€”" where an em dash should be. The CLIs (node, python)
    /// emit UTF-8 no matter what the console codepage says, and every one of the ELEVEN spawn
    /// sites decoded with the OS default — right on Linux by coincidence, wrong on every Windows
    /// box. Same failure class as v0.3.8.53's CreateNoWindow cascade: an invariant that lives at
    /// eleven call sites is not an invariant, it is eleven separate chances to forget. So the rule
    /// is scanned, not remembered: a file that redirects a pipe names the encoding it decodes with.
    /// </summary>
    [Fact]
    public void EveryRedirectedPipe_DecodesAsUtf8()
    {
        var problems = new List<string>();
        foreach (var file in ProductionSources())
        {
            var source = File.ReadAllText(file);
            if (!source.Contains("RedirectStandardOutput = true")) continue;
            if (!source.Contains("StandardOutputEncoding = Encoding.UTF8"))
                problems.Add($"{Path.GetFileName(file)} redirects stdout without declaring UTF-8 decoding");
            if (source.Contains("RedirectStandardError = true")
                && !source.Contains("StandardErrorEncoding = Encoding.UTF8"))
                problems.Add($"{Path.GetFileName(file)} redirects stderr without declaring UTF-8 decoding");
        }
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
    /// <summary>
    /// v0.3.8.56 — the director's last catch cannot itself crash the process.
    ///
    /// Loop() runs on a plain background Thread, where an escaping exception does not fail a
    /// mission — it kills the whole process. The catch handler logged the error through the SAME
    /// database whose failure it may be handling: in CI, teardown deleted the temp colony, the
    /// idle log threw, the handler re-threw the identical SqliteException, and the test host died
    /// with 438 tests green. So the rule is structural: the "Director loop error" log must sit
    /// inside its own try, nearer than the enclosing catch it serves.
    /// </summary>
    [Fact]
    public void TheDirectorsLastCatch_CannotItselfCrashTheProcess()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "ColonyDirector.cs"));
        var at = src.IndexOf("Director loop error", StringComparison.Ordinal);
        Assert.True(at > 0, "the director loop's catch handler (its error log) has moved or been removed");
        var before = src[..at];
        var nearestTry = before.LastIndexOf("try", StringComparison.Ordinal);
        var nearestCatch = before.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(nearestTry > nearestCatch,
            "the catch handler's own LogEvent must sit inside a nested try: it writes to the same "
            + "database whose failure it may be handling, and Loop() runs on a bare thread where "
            + "an escaping exception kills the entire process");
    }
}
