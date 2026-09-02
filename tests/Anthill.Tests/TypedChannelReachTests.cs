using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Every role that talks to a model is ACCOUNTED FOR against the typed channel. v0.3.8.57.
///
/// The artifact store has held typed output since v3.8.20 and `ArtifactContext.Compile` has been
/// bounded and ordered since v3.8.29 — but the compiler reached exactly three roles. Coder, builder
/// and verifier build their prompts through `BuildContextPacketText`, which appends the block; the
/// researcher assembles its own context out of memory, pheromones and tool output and had therefore
/// never seen an artifact in its life. It summarises "what this mission has established" for the
/// coder to work from, so a paraphrase of other workers' narrative about the patch set was feeding
/// the role that writes the next patch.
///
/// WHY A LEDGER AND NOT A SWEEP. A test that asserted "every model call site receives artifacts"
/// would be wrong: the web ant summarises the sources it fetched in that same call, and the scribe
/// writes the OPERATOR-FACING answer, where handing it Colony-visibility material would defeat the
/// visibility classes v3.8.25 introduced. Those are reasons, not oversights, and the difference
/// between the two is exactly what gets lost when a role is quietly missing from a list.
///
/// So the ledger is the artifact: every model-calling role appears with a decision and a reason, and
/// the test fails when the code grows a role the ledger does not mention. A new ant cannot reach a
/// model without someone writing down whether it should see the typed record — which is the failure
/// mode this repository keeps finding, in the form "implemented, tested, and reaching nobody".
/// </summary>
public class TypedChannelReachTests
{
    /// <param name="ReceivesTypedArtifacts">
    /// True means the role's enclosing type must demonstrably wire the block; false means it must
    /// demonstrably NOT, so that a change of mind has to change the ledger too.
    /// </param>
    private sealed record Consumer(
        string Role, string RelativePath, string TypeName, bool ReceivesTypedArtifacts, string Why);

    private static readonly Consumer[] Ledger =
    {
        new("researcher", "src/Anthill.Core/Agents/Ants.cs", "ResearcherAnt", true,
            "Core ant whose brief feeds the coder. Joined the channel in v0.3.8.57; until then its "
          + "entire context was prose and it was summarising narrative about artifacts it could not read."),

        new("coder", "src/Anthill.Core/Agents/Ants.cs", "CoderAnt", true,
            "Writes the patch. Reads the ui_map and file_set the roles before it produced."),

        new("builder", "src/Anthill.Core/Agents/Ants.cs", "BuilderAnt", true,
            "Assembles on top of prior work; the typed record is what prior work actually was."),

        new("verifier", "src/Anthill.Core/Agents/Ants.cs", "VerifierAnt", true,
            "Judges a revision. A verdict formed from prose about a change rather than the change "
          + "is the specific failure v3.8.22 shipped."),

        new("web", "src/Anthill.Core/Agents/Ants.cs", "WebResearchAnt", false,
            "Its prompt summarises the sources fetched in that same call. Mission artifacts are not "
          + "its subject, and spending a fetch-summarisation budget on them would displace the "
          + "material the call exists to condense."),

        new("scribe", "src/Anthill.Core/Orchestration/ResultAssembler.cs", "ResultAssembler", false,
            "Writes the OPERATOR-FACING answer from an already-selected result. Patch sets are stored "
          + "at ArtifactVisibility.Colony precisely so raw proposed source is not published outward "
          + "with the operator summary; an unfiltered compiler here would undo that. A scribe that "
          + "should read artifacts needs a visibility-aware compile, not this one."),

        // Found BY THIS LEDGER on its first run, which is the argument for having one: the call is
        // split across lines, so the hand grep that produced the six entries below it missed it.
        new("strategist", "src/Anthill.Core/Autonomy/Strategist.cs", "Strategist", false,
            "Runs BEFORE a mission exists — it turns a standing Objective into the goal a mission "
          + "will then be planned from, and takes an Objective rather than a Mission, so there is no "
          + "mission whose artifacts could be compiled. The genuine question it raises is a different "
          + "one and is recorded rather than answered: an objective's PREVIOUS runs produced artifacts "
          + "under other mission ids, and ArtifactContext.Compile is per-mission by construction."),

        new("planner", "src/Anthill.Core/Planning/Planner.cs", "Planner", false,
            "Plans before the mission has produced anything, so there is nothing typed to read. "
          + "KNOWN GAP, recorded rather than assumed away: whether a mid-mission replan should "
          + "receive the artifacts produced since the first plan is open and not yet decided."),
    };

    /// <summary>
    /// The source of the type that encloses a role's model call, comments blanked.
    /// </summary>
    private static string TypeBody(Consumer consumer)
    {
        var file = SourceText.CodeOnly(File.ReadAllText(
            Path.Combine(SourceText.RepoRoot(), consumer.RelativePath.Replace('/', Path.DirectorySeparatorChar))));

        // \b at BOTH ends. `IndexOf("class Strategist")` matches `class StrategistResult` — a DTO
        // declared earlier in the same file with no model call in it — so the body examined would
        // have been the wrong type's, and the ledger's claim about the strategist would have been
        // checked against something else entirely and passed.
        var match = System.Text.RegularExpressions.Regex.Match(
            file, $@"\bclass {System.Text.RegularExpressions.Regex.Escape(consumer.TypeName)}\b");
        Assert.True(match.Success, $"{consumer.TypeName} is no longer declared in {consumer.RelativePath}");
        var start = match.Index;

        // Top-level types are declared at column zero, so the next one marks the end.
        var end = file.IndexOf("\npublic ", start + 1, StringComparison.Ordinal);
        return end < 0 ? file[start..] : file[start..end];
    }

    /// <summary>
    /// The ledger covers every role that reaches a model. A role that appears in the code and not
    /// here is the whole point: it got a model call without anyone deciding what it should read.
    /// </summary>
    [Fact]
    public void EveryModelCallingRole_AppearsInTheLedger()
    {
        var roles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            // The router's own declaration is the parameter, not a call.
            if (path.EndsWith("ModelRouter.cs", StringComparison.Ordinal)) continue;

            // v0.3.8.112 — read through the shared resolver, which sees a NAMED CONSTANT as well as
            // a literal. The old regex required a quoted first argument, so `GenerateTyped(Roles.X,
            // …)` would have been a role reaching a model with nobody deciding what it may read —
            // precisely the condition this test exists to make impossible.
            foreach (var role in SourceText.CallArgument(
                         SourceText.CodeOnly(File.ReadAllText(path)), "GenerateTyped", 0,
                         SourceText.ConstantsAcrossSource(SourceText.RepoRoot())))
                roles.Add(role);
        }

        Assert.NotEmpty(roles);

        var declared = Ledger.Select(c => c.Role).ToHashSet(StringComparer.Ordinal);
        var undeclared = roles.Where(r => !declared.Contains(r)).ToList();

        Assert.True(undeclared.Count == 0,
            $"these roles call a model and the typed-channel ledger does not mention them: {string.Join(", ", undeclared)}. "
          + "Add each to Ledger with a decision and a reason — a role reaching a model without one is "
          + "how the researcher spent nine releases unable to read a single artifact.");
    }

    /// <summary>
    /// And the ledger is not a wish list. A role recorded as receiving the block must actually be
    /// wired to one; a role recorded as not receiving it must actually not be.
    /// </summary>
    [Fact]
    public void TheLedgersClaims_MatchTheCode()
    {
        foreach (var consumer in Ledger)
        {
            var body = TypeBody(consumer);

            // The two wirings that exist: through the context packet, or through the shared block
            // helper directly. Both end at ArtifactContext.Compile — one budgeting rule, one filter.
            var wired = body.Contains("artifacts: _artifact", StringComparison.Ordinal)
                     || body.Contains("DomainHelpers.ArtifactBlock(", StringComparison.Ordinal);

            Assert.True(wired == consumer.ReceivesTypedArtifacts,
                consumer.ReceivesTypedArtifacts
                    ? $"the ledger says {consumer.Role} receives typed artifacts, but {consumer.TypeName} "
                      + "no longer wires a block. Either restore the wiring or change the ledger entry — "
                      + "a role listed as reading the typed record while reading prose is worse than one "
                      + "honestly listed as not reading it."
                    : $"the ledger says {consumer.Role} does not receive typed artifacts, but "
                      + $"{consumer.TypeName} now wires a block. That may well be right — say so in the "
                      + $"ledger, because the reason recorded against it is: {consumer.Why}");
        }
    }

    /// <summary>
    /// Every wiring goes through the one budgeting rule. A second copy of "a quarter of the prose
    /// budget, floored at 1,500" is how two components come to disagree about how much room the
    /// structured record gets, and the losing side is always the structured record.
    /// </summary>
    [Fact]
    public void TheArtifactBlock_HasExactlyOneImplementation()
    {
        var compilers = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var code = SourceText.CodeOnly(File.ReadAllText(path));
            if (code.Contains("ArtifactContext.Compile(", StringComparison.Ordinal)) compilers.Add(path);
        }

        var names = compilers.Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(names.Count == 1 && names[0] == "DomainHelpers.cs",
            "ArtifactContext.Compile should be called from DomainHelpers.ArtifactBlock and nowhere else, "
          + "so the budget and the declared-input filter are decided in one place. Callers found: "
          + string.Join(", ", names));
    }
}
