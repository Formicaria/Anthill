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
        var queen = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs"));
        var constructed = Regex.Matches(queen, @"new\s+([A-Za-z]+Tool)\s*\(")
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
            "These tools are declared implemented but never constructed in Queen.BuildToolRegistry, "
          + "so a role allowed them would be told the tool does not exist: " + string.Join(", ", missing));
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
}
