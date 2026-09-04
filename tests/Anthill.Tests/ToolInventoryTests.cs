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
        // v3.8.16 — the composition root became two, and this guard reads both.
        //
        // Queen.BuildToolRegistry composes the tools that stayed in the core; ToolsModule.Register
        // composes the six that moved to Anthill.Modules.Tools. The registration CALL differs
        // between them — `registry.Register(new X(` versus `Offer(new X(` — so both shapes are
        // matched, and the count assertion below is what catches a rename that makes one of them
        // silently match nothing.
        var queenBody = Regex.Match(
            File.ReadAllText(Path.Combine(Root(), "src", "Anthill.Core", "Orchestration", "Queen.cs")),
            @"private ToolRegistry BuildToolRegistry\(.*?\n    \}", RegexOptions.Singleline);
        Assert.True(queenBody.Success, "BuildToolRegistry is no longer shaped the way this guard reads it.");

        var moduleBody = File.ReadAllText(Path.Combine(
            Root(), "src", "Anthill.Modules", "Anthill.Modules.Tools", "ToolsModule.cs"));

        var registeredTypes = Regex.Matches(queenBody.Value, @"registry\.Register\(new (\w+)\(")
            .Concat(Regex.Matches(moduleBody, @"Offer\(new (\w+)\("))
            .Select(m => m.Groups[1].Value).ToList();
        Assert.True(registeredTypes.Count >= 5, $"Only found {registeredTypes.Count} registrations.");

        // The tool TYPES are now split across two directories, so both are read to find each one's
        // Name literal. A type in neither still fails the lookup below.
        var toolSources = string.Concat(
            Directory.GetFiles(Path.Combine(Root(), "src", "Anthill.Core", "Tools"), "*.cs")
                .Concat(Directory.GetFiles(
                    Path.Combine(Root(), "src", "Anthill.Modules", "Anthill.Modules.Tools"), "*.cs"))
                .Select(File.ReadAllText));

        var registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in registeredTypes)
        {
            // SCOPED TO THE TYPE'S OWN BODY, and v0.3.8.106 had to fix that before it could widen
            // anything. The lookup ran `class {type}.*?public string Name => "..."` with a lazy
            // match over EVERY tool file concatenated together — so for a type whose name is not a
            // literal, the match simply ran on into the NEXT class and returned ITS name. The guard
            // could attribute one tool's name to another type, and stayed correct only because
            // every registered type happened to declare a literal shortly after itself. A tool that
            // named itself through a const was all it took to expose that.
            //
            // The body is everything from this declaration to the next `class X : ITool`, so a name
            // can only ever be read from the type that declares it.
            var declaration = Regex.Match(toolSources, $@"class {type}\s*:\s*ITool");
            Assert.True(declaration.Success, $"Could not find the declaration of registered tool type {type}.");

            var after = toolSources[(declaration.Index + declaration.Length)..];
            var next = Regex.Match(after, @"class \w+\s*:\s*ITool");
            var body = next.Success ? after[..next.Index] : after;

            // A LITERAL, or a CONST THIS TYPE DECLARES. The second half is the widening: naming a
            // tool once as a const is what lets the inventory, the authorization table, the role
            // contract and the dispatch chokepoint share one spelling. Widening where the guard
            // LOOKS, never what it accepts — an unresolvable name still fails.
            var resolved =
                Regex.Match(body, @"public string Name => ""([^""]+)""") is { Success: true } literal
                    ? literal.Groups[1].Value
                    : Regex.Match(body, @"public string Name => (\w+);") is { Success: true } forwarded
                        && Regex.Match(body, $@"const string {forwarded.Groups[1].Value} = ""([^""]+)"";")
                            is { Success: true } declared
                        ? declared.Groups[1].Value
                        : null;

            Assert.True(resolved is not null,
                $"Could not resolve the tool name for registered type {type} — its body declares "
              + "neither a string literal nor a const naming it, so nothing can check it against "
              + "ToolInventory.");
            registeredNames.Add(resolved!);
        }

        // v0.3.8.102 — a THIRD composition site. The system-action tools are adopted by the API
        // host (ApiHost.Actions.cs), not by the Queen, because they wrap the homelab ActionExecutor
        // and only exist where a homelab repository exists. Their Name properties forward the SDK
        // constants rather than holding string literals — the module may not name Core, and the
        // SDK is where shared names live — so the literal-lookup regex above cannot read them.
        // The guard instead asserts the adoption call is present and admits the two SDK names.
        var apiActionsBody = File.ReadAllText(Path.Combine(
            Root(), "src", "Anthill.Api", "Homelab", "ApiHost.Actions.cs"));
        Assert.Contains("SystemActionTools.For(", apiActionsBody);
        registeredNames.Add(Anthill.SDK.Contracts.SystemActionToolNames.Propose);
        registeredNames.Add(Anthill.SDK.Contracts.SystemActionToolNames.Execute);

        // v0.3.8.103 — and a FOURTH: the send tools are composed by the API host itself
        // (ApiHost.cs), beside the module tools rather than with the homelab's, because the
        // adapter reads operator configuration rather than a homelab repository. Same reason the
        // literal-lookup regex cannot read them: their Name properties forward the SDK constants.
        var apiHostBody = File.ReadAllText(Path.Combine(
            Root(), "src", "Anthill.Api", "ApiHost.cs"));
        Assert.Contains("ExternalActionTools.For(", apiHostBody);
        registeredNames.Add(Anthill.SDK.Contracts.ExternalActionToolNames.Propose);
        registeredNames.Add(Anthill.SDK.Contracts.ExternalActionToolNames.Execute);

        // v0.3.8.121 — a FIFTH composition site. The knowledge tools are registered by
        // Anthill.Modules.Knowledge, whose module file this regex does not scan (it reads Queen.cs
        // and ToolsModule.cs by name, deliberately, so a `new XTool(` in a test satisfies nothing).
        // Their Name properties forward SDK constants for the usual reason — the module may not
        // name Core — so the literal lookup cannot read them either.
        //
        // Unlike every block above, this one is CONDITIONAL, and the condition is the point:
        // KnowledgeModule.Register returns without registering anything when knowledge is disabled,
        // which is how Rule 15 holds. So the guard asserts the registration site exists and admits
        // the declared vocabulary, rather than asserting six tools are live in a build that ships
        // with the feature off.
        var knowledgeModuleBody = File.ReadAllText(Path.Combine(
            Root(), "src", "Anthill.Modules", "Anthill.Modules.Knowledge", "KnowledgeModule.cs"));
        Assert.Contains("Offer(new KnowledgeSearchTool(", knowledgeModuleBody);
        Assert.Contains("Offer(new KnowledgeReviewTool(", knowledgeModuleBody);
        foreach (var name in Anthill.SDK.Knowledge.KnowledgeToolNames.All)
            registeredNames.Add(name);

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
    ///
    /// v3.5.0: SCRIBE CAME OFF THIS LIST. read_changed_files_summary was built as a scoped workspace
    /// tool, so the role that writes release notes can dispatch something for the first time.
    ///
    /// v3.8.23: THE LIST IS NOW EMPTY, and the way it emptied is the point. The three remaining
    /// roles — soldier, medic, archivist — did not get their tools built. Their contracts stopped
    /// naming tools that do not exist, because on inspection none of the three should have been a
    /// tool: soldier's PolicyScan is a deterministic in-process service and belongs out of a model's
    /// reach, medic needs orchestration to ASSEMBLE a typed failure context rather than a tool to go
    /// fetch one, and archivist's write path already exists through artifacts and would have been
    /// duplicated. Building all three would have produced the same green with more attack surface
    /// and one redundant write channel.
    ///
    /// The assertion is now "empty", which is stronger than the old pin: a role newly blocked on an
    /// unbuilt tool fails the build immediately rather than being added to a tolerated list.
    /// </summary>
    [Fact]
    public void NoRoleIsBlockedByAnUnbuiltTool()
    {
        var blocked = ToolInventory.RolesBlockedByMissingTools(AntExecutionCatalog.Contracts);

        Assert.Empty(blocked);

        // tester is the control: its one allowed tool is real, so a contract is not INHERENTLY
        // blocking. Without this the assertion above would still pass if contracts stopped working
        // entirely, and would be measuring nothing.
        Assert.True(ToolInventory.Exists("run_allowlisted_check"));
        Assert.Contains("run_allowlisted_check", AntExecutionCatalog.Contracts["tester"].AllowedTools);
    }

    /// <summary>
    /// The Planned list is empty and must STAY load-bearing. A contract naming a tool that is in
    /// neither Implemented nor Planned is a phantom, and this is the guard that catches the next one
    /// the moment it is written rather than three releases later.
    /// </summary>
    [Fact]
    public void NoContractNamesAToolThatDoesNotExist()
    {
        var phantoms = AntExecutionCatalog.Contracts
            .SelectMany(kv => kv.Value.AllowedTools.Select(t => (Role: kv.Key, Tool: t)))
            .Where(x => !ToolInventory.Exists(x.Tool) && !ToolInventory.Planned.Contains(x.Tool))
            .ToList();

        Assert.True(phantoms.Count == 0,
            "These contracts name tools nothing implements: "
            + string.Join(", ", phantoms.Select(p => $"{p.Role}->{p.Tool}")));
    }
}
