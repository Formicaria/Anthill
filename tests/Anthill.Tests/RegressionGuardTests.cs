using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v1.8.28 validation/regression harness (NORTH_STAR Phase 2). Repo-level guards for bug classes
/// that have already shipped once: version-marker drift, non-idempotent schema migration, UI glyph
/// corruption, and stray active Python. These run in plain `dotnet test`, so they gate local work
/// and CI identically. See docs/NORTH_STAR.md §4.
/// </summary>
public class RegressionGuardTests : IDisposable
{
    private readonly string _tmpDir;

    public RegressionGuardTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "anthill_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    /// <summary>Walk up from the test bin directory to the repo root (marked by Anthill.sln).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate repo root (Anthill.sln) above the test bin directory.");
        return dir!.FullName;
    }

    // ---- Version marker consistency ----------------------------------------------------------
    // The repo shipped with Directory.Build.props stuck at 1.8.15.6 while the runtime said 1.8.27.
    // Every version marker must agree: runtime const, assembly version, README, and CHANGELOG.

    [Fact]
    public void VersionMarkers_RuntimeMatchesDirectoryBuildProps()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var m = Regex.Match(props, @"<AnthillVersion>([^<]+)</AnthillVersion>");
        Assert.True(m.Success, "Directory.Build.props has no <AnthillVersion> marker.");
        Assert.Equal(AnthillRuntime.Version, m.Groups[1].Value.Trim());
    }

    [Fact]
    public void VersionMarkers_ReadmeAdvertisesRuntimeVersion()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        var m = Regex.Match(readme, @"\*\*Current version:\*\*\s*v([0-9][0-9A-Za-z.\-]*)");
        Assert.True(m.Success, "README.md has no '**Current version:** vX.Y.Z' marker.");
        Assert.Equal(AnthillRuntime.Version, m.Groups[1].Value.Trim());
    }

    [Fact]
    public void VersionMarkers_ChangelogHasEntryForRuntimeVersion()
    {
        var changelog = File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md"));
        var pattern = @"^##\s+v" + Regex.Escape(AnthillRuntime.Version) + @"\b";
        Assert.True(Regex.IsMatch(changelog, pattern, RegexOptions.Multiline),
            $"CHANGELOG.md has no '## v{AnthillRuntime.Version}' entry for the current runtime version.");
    }

    // ---- Migration idempotence -----------------------------------------------------------------
    // Schema creation must be safe on a fresh DB, on an existing DB, and when re-run repeatedly.

    [Fact]
    public void Migration_FreshDb_CreatesSchemaAndReportsTables()
    {
        var dbPath = Path.Combine(_tmpDir, "fresh.db");
        using var mem = new SqliteMemory(dbPath);
        var counts = mem.TableCounts();
        Assert.NotEmpty(counts);
    }

    [Fact]
    public void Migration_ExistingDb_ReopenIsIdempotent()
    {
        var dbPath = Path.Combine(_tmpDir, "reopen.db");
        Dictionary<string, object?> first;
        using (var mem = new SqliteMemory(dbPath))
            first = mem.TableCounts();
        // Re-opening runs InitDb again over the existing schema — must not throw or change shape.
        using (var mem = new SqliteMemory(dbPath))
        {
            var second = mem.TableCounts();
            Assert.Equal(first.Keys.OrderBy(k => k), second.Keys.OrderBy(k => k));
        }
    }

    [Fact]
    public void Migration_RepeatedReruns_AllPass()
    {
        var dbPath = Path.Combine(_tmpDir, "rerun.db");
        for (var i = 0; i < 3; i++)
        {
            using var mem = new SqliteMemory(dbPath);
            Assert.NotEmpty(mem.TableCounts());
        }
    }

    // ---- UI glyph/encoding integrity -------------------------------------------------------------
    // An editor in the pipeline has repeatedly re-saved the embedded UI as non-UTF-8, flattening
    // icon glyphs to '?' / U+FFFD. Mirror of the CI ui-integrity job so `dotnet test` catches it too.

    [Fact]
    public void UiIntegrity_NoFlattenedGlyphsOrReplacementChars()
    {
        var ui = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui", "index.html"));
        var problems = new List<string>();

        var fffd = ui.Count(c => c == '�');
        if (fffd > 0) problems.Add($"{fffd} U+FFFD replacement char(s)");

        var bare = Regex.Matches(ui, @"(?<!<kbd)>\?<").Count; // bare >?< icons, excluding <kbd>?</kbd>
        if (bare > 0) problems.Add($"{bare} bare '>?<' icon glyph(s) flattened to '?'");

        var labeled = Regex.Matches(ui, @">\s*\?\s+[A-Z][a-z]").Count; // '>? Label' buttons
        if (labeled > 0) problems.Add($"{labeled} '>? Label' button glyph(s)");

        var ternary = Regex.Matches(ui, Regex.Escape("'?':'?'")).Count; // caret ternaries
        if (ternary > 0) problems.Add($"{ternary} \"'?':'?'\" caret ternary(ies)");

        Assert.True(problems.Count == 0,
            "UI encoding corruption in src/Anthill.Api/Ui/index.html: " + string.Join("; ", problems));
    }

    /// <summary>
    /// v1.9.1.1: the UI title/header versions were hardcoded markup and silently drifted
    /// (stuck at v1.8.29.1 while the runtime said v1.9.1). The UI must render the version it
    /// fetches from /health — never a literal version baked into the HTML.
    /// </summary>
    [Fact]
    public void UiIntegrity_NoHardcodedVersionInMarkup()
    {
        var ui = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui", "index.html"));
        var hardcoded = Regex.Matches(ui, @">\s*v\d+\.\d+[\d.]*\s*<");
        Assert.True(hardcoded.Count == 0,
            "Hardcoded version string(s) in UI markup (must come from /health at runtime): "
            + string.Join("; ", hardcoded.Select(m => m.Value.Trim())));
        Assert.DoesNotContain("<title>ANTHILL v", ui);
    }

    /// <summary>
    /// v2.2.6: release mishaps have twice tagged a version whose CHANGELOG top entry was older
    /// (rebase ordering). The FIRST '## vX.Y.Z' heading must be the current runtime version.
    /// </summary>
    [Fact]
    public void VersionMarkers_ChangelogTopEntryIsRuntimeVersion()
    {
        var changelog = File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md"));
        var m = Regex.Match(changelog, @"^##\s+v([0-9][0-9A-Za-z.\-]*)", RegexOptions.Multiline);
        Assert.True(m.Success, "CHANGELOG.md has no '## vX.Y.Z' entries.");
        Assert.Equal(AnthillRuntime.Version, m.Groups[1].Value.Trim());
    }

    /// <summary>
    /// v2.2.6: the retired System Core panel was deleted while legacy code still looked its
    /// elements up (silent dead work). Every getElementById target must exist as a static id in
    /// the markup, ids created at runtime are allow-listed, and no id may be declared twice.
    /// </summary>
    [Fact]
    public void UiIntegrity_NoOrphanedElementLookupsAndNoDuplicateIds()
    {
        var ui = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui", "index.html"));
        var dynamicIds = new HashSet<string> { "pc-toast" }; // created via document.createElement at runtime

        var declared = Regex.Matches(ui, "id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        var duplicates = declared.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate element id(s) in UI markup: " + string.Join(", ", duplicates));

        var declaredSet = declared.ToHashSet();
        var orphans = Regex.Matches(ui, @"getElementById\('([^']+)'\)")
            .Select(m => m.Groups[1].Value).Distinct()
            .Where(id => !declaredSet.Contains(id) && !dynamicIds.Contains(id))
            .OrderBy(id => id).ToList();
        Assert.True(orphans.Count == 0,
            "getElementById target(s) with no matching id= in markup (orphaned lookups): " + string.Join(", ", orphans));
    }

    /// <summary>
    /// v2.2.3 shipped 'Other · 25': the registry serializes PascalCase but a UI adapter read only
    /// camelCase, classifying the whole colony as Other. The adapter accessors must stay
    /// case-tolerant (both casings listed) so a serializer/policy change can never silently
    /// unclassify the colony again.
    /// </summary>
    [Fact]
    public void UiIntegrity_RegistryAdapterAccessorsAreCaseTolerant()
    {
        var ui = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui", "index.html"));
        Assert.Contains("prop(r,'roleId','RoleId'", ui);
        Assert.Contains("prop(w,'workerId','WorkerId'", ui);
        Assert.Contains("prop(r,'workers','Workers')", ui);
    }

    // ---- No active Python ------------------------------------------------------------------------
    // py.old/ is archived history. No .py file may exist anywhere else in the repo.

    [Fact]
    public void NoPython_NoPyFilesOutsidePyOld()
    {
        var root = RepoRoot();
        var offenders = Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(p => !p.StartsWith("py.old/", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("/bin/") && !p.Contains("/obj/") && !p.StartsWith(".git/"))
            .ToList();
        Assert.True(offenders.Count == 0,
            "Active Python files found outside py.old/ (forbidden by NORTH_STAR §3.1 rule 13): "
            + string.Join(", ", offenders));
    }
}
