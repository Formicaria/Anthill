using Anthill.Core.Agents;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE TREE ENFORCES ITS OWN RULES. v0.3.8.112, PLAN.md §2b `.112` — R0's last item.
///
/// WHAT R0 ASKED FOR: "warnings as errors, analyzers, dependency and secret scanning, a complexity
/// budget, module auto-discovery, and the guard hierarchy written down." Some of that is build
/// configuration and lives in `Directory.Build.props`. The rest is here, because a rule about the
/// SHAPE of this repository is a rule this repository's own suite should refuse to violate — the
/// same reasoning that put the version-marker check in `RegressionGuardTests` rather than in a
/// script somebody remembers to run.
///
/// WHAT THIS DELIBERATELY IS NOT. It is not a linter, and it does not have opinions about style.
/// Every assertion below is keyed to a specific failure this project can actually suffer: a
/// credential committed to source, a file that has grown past the point where anyone reads it whole,
/// a package version that drifted between three copies of itself. A guard that fired on taste would
/// be argued with, then suppressed, then deleted.
///
/// THE BUDGETS ARE MEASURED, NOT INVENTED. Every threshold here was computed from the tree as it
/// stood at `.112` and set with headroom above the worst real case, so this release does not smuggle
/// in a refactor. A budget set below what the code already does is not a budget, it is a backlog
/// with a build failure attached.
/// </summary>
public class RepositoryEnforcementTests
{
    private static string Root => SourceText.RepoRoot();

    private static IEnumerable<string> ProductionFiles() => SourceText.ProductionFiles(Root);

    // -------------------------------------------------------------------------------------------
    // Warnings as errors
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE FLAG IS ON, AND A TEST SAYS SO BECAUSE A BUILD SETTING IS THE EASIEST THING IN A
    /// REPOSITORY TO TURN OFF QUIETLY.
    ///
    /// One line in one file, edited to unblock one awkward build, reverts an R0 item with no diff
    /// anybody reviews as a policy change. The census that justified turning it on returned zero
    /// warnings across the solution; this is what makes turning it back off a conversation.
    /// </summary>
    [Fact]
    public void WarningsAreErrors_ForTheWholeSolution()
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));

        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", props, StringComparison.Ordinal);
        Assert.DoesNotContain("<NoWarn>", props, StringComparison.Ordinal);

        // And no project quietly exempts itself. A per-project override is invisible from the root
        // file, which is exactly where anyone would look to check that this rule holds.
        var exempt = Directory.GetFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => File.ReadAllText(p) is var text
                     && (text.Contains("<TreatWarningsAsErrors>false", StringComparison.Ordinal)
                      || text.Contains("<NoWarn>", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(exempt.Count == 0,
            "these projects exempt themselves from warnings-as-errors: " + string.Join(", ", exempt)
          + ". An exemption is a decision about the whole tree's standard taken in one project's "
          + "file, where nobody checking the standard will see it.");
    }

    // -------------------------------------------------------------------------------------------
    // Secret scanning
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// NO CREDENTIAL IS COMMITTED TO THIS REPOSITORY.
    ///
    /// REUSES `PolicyScan`'s RULE TABLE rather than writing a second one, which is this project's
    /// standing rule and matters more than usual here: a secret scanner is exactly the kind of thing
    /// that acquires a private copy of its patterns, and two copies means the one that is maintained
    /// is not always the one that runs. The colony already scans PATCHES with these rules; this
    /// points the same rules at the tree they get applied to.
    ///
    /// THE EXEMPTIONS ARE THE INTERESTING PART, and they are the reason a naive version of this test
    /// fails immediately. This codebase documents its defects IN COMMENTS by quoting them, and the
    /// files that describe secret handling necessarily contain secret-shaped text. `CodeOnly` blanks
    /// comments, which removes most of it — what remains is the pattern tables and the fixtures,
    /// named individually below. A directory-wide exemption would be the wrong shape: it would grow
    /// silently, and the point of a scanner is that nothing is quietly outside it.
    /// </summary>
    [Fact]
    public void NoSecretShapedLiteral_IsCommittedToSource()
    {
        // Files whose SUBJECT is secret detection, and which therefore contain the patterns and the
        // fixtures by construction. Named one by one, so adding a file to this list is a decision.
        var byDesign = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PolicyScan.cs",              // the rule table itself
            "SecretPatternEncodingTests.cs",
            "SecretArtifactTests.cs",
            "StageBConsequentialTests.cs",
            "SoldierBlockLifecycleTests.cs",
            "RepositoryEnforcementTests.cs",   // this file names the rule id below
        };

        var offenders = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in ProductionFiles())
        {
            var name = Path.GetFileName(file);
            if (byDesign.Contains(name)) continue;

            // Comments are blanked, so a paragraph explaining a credential defect is not a credential.
            var code = SourceText.CodeOnly(File.ReadAllText(file));

            foreach (var finding in PolicyScan.Scan(code))
                if (string.Equals(finding.RuleId, "secret_material", StringComparison.Ordinal))
                    offenders.TryAdd(name, finding.Detail);
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} source file(s) contain secret-shaped literals:\n  "
          + string.Join("\n  ", offenders.Select(kv => $"{kv.Key}  {kv.Value}"))
          + "\nA committed credential is not fixed by rotating it later — it is in the history from "
          + "the moment it is pushed. If this is a fixture rather than a secret, the honest fix is "
          + "to make it obviously not one, not to add the file to the exemption list.");
    }

    /// <summary>
    /// AND THE SCANNER STILL WORKS. The assertion above passes when the tree is clean AND when the
    /// scanner has stopped scanning — the vacuity failure this suite has now caught in four separate
    /// forms. This runs a value that must be found through the exact path the sweep uses.
    /// </summary>
    [Fact]
    public void TheSecretScanner_StillFindsOne()
    {
        // The rule requires the VALUE to be quoted — `password = "…"` — which is what a committed
        // credential actually looks like and what the first draft of this fixture got wrong: it
        // planted `password = hunter2placeholder` unquoted, matched nothing, and would have reported
        // a working scanner as broken. Composed rather than escaped, so the shape is legible.
        const string quote = "\"";
        var planted = SourceText.CodeOnly("password = " + quote + "hunter2placeholder" + quote);

        Assert.Contains(PolicyScan.Scan(planted),
            f => string.Equals(f.RuleId, "secret_material", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------------------------
    // Complexity budget
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// NO SOURCE FILE GROWS WITHOUT SOMEONE DECIDING IT SHOULD.
    ///
    /// THE BUDGET IS MEASURED. At `.112` the largest production file is `ExecutionService.cs` at
    /// 2,912 lines and the next is `ApiHost.Providers.cs` at 1,917. The cap is set at 3,200 — above
    /// the worst real case with room to work, and below the point where the largest file could
    /// double. This is a RATCHET, not a refactor: it stops the tree getting worse, and it does not
    /// pretend that setting a number fixes what is already large.
    ///
    /// WHY LINES AND NOT CYCLOMATIC COMPLEXITY. A real complexity metric needs a parser, and this
    /// suite reads source as text by design — `SourceText`'s own remarks explain why, and a guard
    /// that half-parses C# would be the "adjacent question" defect at its worst, since it would look
    /// like a rigorous measurement. Line count is a crude proxy and it is HONEST about being one:
    /// nobody reads a 3,000-line file whole, whatever its branching factor.
    ///
    /// COMMENTS COUNT, deliberately, and that is a real decision rather than an oversight. This
    /// codebase is built on long explanatory comments and they are the most valuable thing in it —
    /// but a file nobody can hold in their head is a file nobody can hold in their head, and
    /// splitting it is the answer either way. A budget that excluded comments would reward deleting
    /// the reasoning to stay under it, which is the exact trade `SourceText.CodeOnly`'s own doc
    /// comment warns against.
    /// </summary>
    [Fact]
    public void NoProductionFile_ExceedsTheSizeBudget()
    {
        const int budget = 3_200;

        var over = ProductionFiles()
            .Select(f => (Name: Path.GetFileName(f), Lines: File.ReadAllLines(f).Length))
            .Where(x => x.Lines > budget)
            .OrderByDescending(x => x.Lines)
            .ToList();

        Assert.True(over.Count == 0,
            $"these files exceed the {budget}-line budget:\n  "
          + string.Join("\n  ", over.Select(x => $"{x.Name}  {x.Lines}"))
          + "\nThis is a ratchet set above the tree as it stood at v0.3.8.112. Split the file, or "
          + "raise the budget deliberately and say in the same commit why the larger number is the "
          + "right one — but do not raise it as a side effect of landing something else.");
    }

    /// <summary>
    /// THE BUDGET IS NOT VACUOUS. A cap nothing approaches enforces nothing, and would go on
    /// "passing" if `ProductionFiles` stopped returning files at all — which is how a guard comes to
    /// certify an empty set. This pins that the sweep sees a real tree and that the budget is within
    /// sight of it.
    /// </summary>
    [Fact]
    public void TheSizeBudget_IsWithinSightOfTheTree()
    {
        var files = ProductionFiles().Select(f => File.ReadAllLines(f).Length).ToList();

        Assert.True(files.Count >= 200,
            $"only {files.Count} production files were swept; the budget is enforcing almost nothing.");
        Assert.True(files.Max() > 2_000,
            $"the largest production file is {files.Max()} lines. If the tree really has shrunk that "
          + "far, lower the budget to match it — a cap three times the largest file is decoration.");
    }

    // -------------------------------------------------------------------------------------------
    // Dependency pinning
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// ONE VERSION PER PACKAGE, ACROSS EVERY PROJECT.
    ///
    /// THE DEFECT THIS CATCHES is `.112`'s own survey finding: `Microsoft.NET.Test.Sdk`, `xunit` and
    /// `xunit.runner.visualstudio` each have their version string written out THREE times, once per
    /// test project. Three copies of one fact is this repository's most-named defect class, and here
    /// it fails in the quietest possible way — two projects on different versions of a test runner
    /// produce results that disagree for a reason nothing reports.
    ///
    /// A GUARD RATHER THAN CENTRAL PACKAGE MANAGEMENT, and that is a deliberate stopping point.
    /// `Directory.Packages.props` is the real fix and it changes how every project resolves its
    /// references at restore time — a change whose failure mode is "nothing builds", which is not
    /// something to land in the same release as six guard rewrites. This catches the drift today;
    /// §2c carries the move.
    /// </summary>
    [Fact]
    public void EveryPackage_HasOneVersionAcrossTheSolution()
    {
        var versions = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in Directory.GetFiles(Root, "*.csproj", SearchOption.AllDirectories))
        {
            if (project.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || project.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            foreach (System.Text.RegularExpressions.Match reference in
                     System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(project),
                         @"<PackageReference\s+Include=""(?<id>[^""]+)""\s+Version=""(?<version>[^""]+)"""))
            {
                var id = reference.Groups["id"].Value;
                if (!versions.TryGetValue(id, out var set)) versions[id] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(reference.Groups["version"].Value);
            }
        }

        var divergent = versions.Where(kv => kv.Value.Count > 1)
            .Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}")
            .ToList();

        Assert.True(divergent.Count == 0,
            "these packages are referenced at more than one version:\n  " + string.Join("\n  ", divergent)
          + "\nTwo projects on two versions of one dependency disagree for a reason nothing reports.");

        Assert.True(versions.Count >= 5,
            $"only {versions.Count} package references were parsed from the solution's projects, "
          + "so this guard is comparing almost nothing. The reference shape has changed.");
    }
}
