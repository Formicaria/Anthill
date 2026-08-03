using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.4.0 (ADR-006) — the tool vocabulary is one vocabulary.
///
/// Tool names lived as bare strings in three places that never met: what `Queen.BuildToolRegistry`
/// registers, what `ToolAuthorization` lets each role dispatch, and what the specialist
/// `AntExecutionContract`s declare. Nothing compared them, and they had drifted in the two ways
/// string vocabularies always drift.
///
/// Both failures are SILENT, which is why they lasted. A forbid-list naming a tool that does not
/// exist denies nothing and looks like a security boundary. A role allowed only tools nobody built
/// is authorized to do nothing, and presents as an ant that runs and produces no work — not as an
/// error anyone would investigate.
/// </summary>
public class ToolInventoryTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>
    /// The inventory must match what the composition root actually registers.
    ///
    /// A source guard, because the alternative — standing up a Queen — makes the answer depend on
    /// config gates, and the property under test is "does this list describe the build", not "what
    /// did this run enable". If the two ever disagree the inventory is fiction, and everything else
    /// in this file is checking fiction against fiction.
    /// </summary>
    [Fact]
    public void TheInventory_MatchesWhatTheCompositionRootRegisters()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Anthill.Core", "Orchestration", "Queen.cs"));
        var body = Regex.Match(source, @"private ToolRegistry BuildToolRegistry\(.*?\n    \}", RegexOptions.Singleline);
        Assert.True(body.Success, "BuildToolRegistry is no longer shaped the way this guard reads it.");

        // Each `registry.Register(new XyzTool(...))` maps back to the tool's own Name literal.
        var registeredTypes = Regex.Matches(body.Value, @"registry\.Register\(new (\w+)\(")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.True(registeredTypes.Count >= 5, $"Only found {registeredTypes.Count} registrations.");

        var toolSources = string.Concat(
            Directory.GetFiles(Path.Combine(Root(), "src", "Anthill.Core", "Tools"), "*.cs").Select(File.ReadAllText));

        var registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in registeredTypes)
        {
            var name = Regex.Match(toolSources,
                $@"class {type}\s*:\s*ITool.*?public string Name => ""([^""]+)""", RegexOptions.Singleline);
            Assert.True(name.Success, $"Could not find the Name literal for registered tool type {type}.");
            registeredNames.Add(name.Groups[1].Value);
        }

        var missing = registeredNames.Except(ToolInventory.Implemented, StringComparer.OrdinalIgnoreCase).ToList();
        var phantom = ToolInventory.Implemented.Except(registeredNames, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(missing.Count == 0,
            "These tools are registered but absent from ToolInventory.Implemented, so contracts "
          + "referencing them will be reported as unbuilt: " + string.Join(", ", missing));
        Assert.True(phantom.Count == 0,
            "ToolInventory.Implemented claims tools the composition root never registers: "
          + string.Join(", ", phantom));
    }

    /// <summary>
    /// The defect that prompted this file: every specialist contract forbade <c>"shell"</c> and
    /// <c>"write_file"</c>, and neither is a tool name. The real names are <c>shell_command</c> and
    /// <c>write_text_file</c>.
    ///
    /// It was harmless only by coincidence — <c>MissionAgentForbidden</c> covers the same three
    /// tools under their correct names, so the boundary held while the declaration meant nothing.
    /// A rule that is enforced somewhere else by luck is not a rule.
    /// </summary>
    [Fact]
    public void EveryForbiddenTool_IsARealTool()
    {
        var fictional = AntExecutionCatalog.Contracts
            .SelectMany(kv => kv.Value.ForbiddenTools.Select(t => (role: kv.Key, tool: t)))
            .Where(x => !ToolInventory.Exists(x.tool))
            .Select(x => $"{x.role} forbids '{x.tool}'")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(fictional.Count == 0,
            "A forbid-list naming a tool that does not exist denies nothing while looking like a "
          + "boundary:\n  " + string.Join("\n  ", fictional));
    }

    /// <summary>
    /// The structurally-forbidden set is the one boundary no contract can widen, so every name in
    /// it must be real for exactly the same reason.
    /// </summary>
    [Fact]
    public void EveryStructurallyForbiddenTool_IsARealTool()
    {
        var fictional = ToolAuthorization.MissionAgentForbidden.Where(t => !ToolInventory.Exists(t)).ToList();
        Assert.True(fictional.Count == 0,
            "MissionAgentForbidden names tools that do not exist: " + string.Join(", ", fictional));
    }

    /// <summary>
    /// Every allowed tool is either built or explicitly declared as not-yet-built. The point is that
    /// there is no third category — a name that is neither is a typo, and a typo in an allowlist
    /// removes a capability silently.
    /// </summary>
    [Fact]
    public void EveryAllowedTool_IsEitherBuiltOrKnowinglyPlanned()
    {
        var unaccounted = AntExecutionCatalog.Contracts
            .SelectMany(kv => kv.Value.AllowedTools.Select(t => (role: kv.Key, tool: t)))
            .Where(x => !ToolInventory.Exists(x.tool) && !ToolInventory.Planned.Contains(x.tool))
            .Select(x => $"{x.role} allows '{x.tool}'")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(unaccounted.Count == 0,
            "These allowed tools are neither implemented nor listed as planned, which is how a typo "
          + "in an allowlist becomes a role that silently cannot work:\n  "
          + string.Join("\n  ", unaccounted));
    }

    /// <summary>
    /// A tool cannot be both built and planned. If a planned tool ships and nobody removes it from
    /// the planned list, the list keeps saying the role is blocked after it was unblocked — the
    /// exact failure this inventory exists to prevent, reintroduced by omission.
    /// </summary>
    [Fact]
    public void NoTool_IsBothImplementedAndPlanned()
    {
        var both = ToolInventory.Planned.Intersect(ToolInventory.Implemented, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(both.Count == 0,
            "These tools now exist and must be removed from ToolInventory.Planned: " + string.Join(", ", both));
    }

    /// <summary>
    /// The roadmap's long-standing "core-ant contracts are blocked on tool-inventory evidence" note,
    /// made executable.
    ///
    /// ToolAuthorization SHORT-CIRCUITS on contract presence — a role with a contract may dispatch
    /// its AllowedTools and nothing else — so a contract allowing only unbuilt tools authorizes the
    /// role to do nothing at all. Four roles are in that state today.
    ///
    /// This test PINS the set rather than demanding it be empty. Demanding empty would fail the
    /// build for work that is legitimately scheduled later; pinning it means the number can only go
    /// down deliberately, and a fifth role joining the list has to be an explicit decision.
    /// </summary>
    [Fact]
    public void TheRolesBlockedByUnbuiltTools_AreExactlyTheKnownOnes()
    {
        var blocked = ToolInventory.RolesBlockedByMissingTools(AntExecutionCatalog.Contracts);

        Assert.Equal(new[] { "archivist", "medic", "scribe", "soldier" }, blocked);

        // tester is the control: its one allowed tool is real, so a contract is not INHERENTLY
        // blocking. Without this the test above would still pass if contracts stopped working
        // entirely, and would be measuring nothing.
        Assert.DoesNotContain("tester", blocked);
        Assert.True(ToolInventory.Exists("run_allowlisted_check"));
    }
}
