using System.Reflection;
using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The runtime bootstrap has already happened by the time any test reads a global. v0.3.8.88.
///
/// WHY THIS EXISTS. `AnthillRuntime.Initialize` is one-shot and `ProjectConfig` writes the on-disk config over
/// fifty-one process-global statics; `Queen`'s constructor calls it. So before v0.3.8.88 the first
/// test in a run that built a Queen silently discarded whatever the tests before it had set, and
/// every test after that kept its own settings because the bootstrap short-circuits.
///
/// A test therefore passed or failed on its POSITION in the run — and, because the values come from
/// a file on the developer's machine, on whose machine it ran. That is what happened at v0.3.8.87:
/// four lifecycle tests whose production code had not changed went red because a new test class
/// shifted collection ordering, and the same four reproduced on the previous tag under the same
/// filter, which is what proved the dependency was pre-existing rather than introduced.
///
/// `TestAssemblyBootstrap` closes it with a `[ModuleInitializer]`. These are the assertions that say
/// so — because a module initializer that silently stopped running would restore the old hazard
/// exactly, and nothing else in the suite would notice until a release cycle was spent on it.
/// </summary>
public class RuntimeBootstrapOrderTests
{
    /// <summary>
    /// `ConfigPath` is set by `Initialize` and by nothing else, so a non-empty value here is proof
    /// the bootstrap ran before this test — which, being a test, ran after the module initializer.
    ///
    /// Reading the SIDE EFFECT rather than an `_initialised` flag is deliberate: the flag is private,
    /// and a guard that reflected at it would be asserting against the mechanism instead of against
    /// the thing the mechanism is for.
    /// </summary>
    [Fact]
    public void TheRuntimeIsBootstrapped_BeforeAnyTestReadsAGlobal()
    {
        Assert.False(string.IsNullOrWhiteSpace(AnthillRuntime.ConfigPath),
            "AnthillRuntime.ConfigPath is empty, so the runtime was never initialized. The module "
          + "initializer in AssemblyBehavior.cs is what guarantees it; if that stopped running, "
          + "every test that saves a config-projected static for restore is back to depending on "
          + "whether some earlier test happened to construct a Queen first.");
    }

    /// <summary>
    /// The initializer is still there, and still an initializer.
    ///
    /// The assertion above passes just as well when the bootstrap was triggered by an earlier test
    /// building a Queen — which is the exact state this release removed. Read from the ATTRIBUTE
    /// rather than from the source text, so renaming the method or the file cannot quietly satisfy
    /// it.
    /// </summary>
    [Fact]
    public void TheBootstrapIsAModuleInitializer_NotAnEarlyTestGettingLucky()
    {
        var initializers = typeof(RuntimeBootstrapOrderTests).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(m => m.GetCustomAttributes()
                .Any(a => a.GetType().Name == "ModuleInitializerAttribute"))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .ToList();

        Assert.True(initializers.Count > 0,
            "no [ModuleInitializer] exists in this assembly any more. Something has to force "
          + "AnthillRuntime.Initialize before the first test, or test outcomes go back to depending "
          + "on run order — see this class's summary for what that cost.");
    }

    /// <summary>
    /// NON-VACUITY, and the number is the point.
    ///
    /// The hazard is proportional to how many globals the bootstrap overwrites. If that set shrank to
    /// nothing the guard above would still pass while guarding nothing; if it grew, the blast radius
    /// grew with it and this file's summary understates it. Either way a reader should be told.
    ///
    /// Counted from `ProjectConfig`'s own source rather than from a list kept here, because a list here
    /// would be a second copy of a fact the method owns — which this repository has now paid for
    /// several times.
    /// </summary>
    [Fact]
    public void TheBootstrapStillOverwritesTheGlobals_ThisGuardIsAbout()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));

        // `ProjectConfig`, not `Initialize`. Initialize is the entry point and delegates; the
        // method that actually writes the statics is this one. The first draft named Initialize,
        // brace-matched a 282-character body, found ZERO assignments and would have failed for a
        // reason that had nothing to do with the hazard — a guard pointed one method away from the
        // thing it guards.
        var at = source.IndexOf("void ProjectConfig(", StringComparison.Ordinal);
        Assert.True(at >= 0,
            "AnthillRuntime.ProjectConfig was renamed; this guard no longer reads the method that "
          + "writes the globals and would pass over nothing.");

        // BRACE-MATCHED TO THE METHOD, not read to end of file. The first draft of this guard sliced
        // `source[at..]` and counted 51 — every `x = config.y` in the rest of the file, most of them
        // in other methods. It would have passed, and it would have been counting something other
        // than what its own message claims. The adjacent-question defect, committed inside a guard
        // written for a release about committing it.
        var open = source.IndexOf('{', at);
        Assert.True(open >= 0, "ProjectConfig has no body this guard can find.");

        var depth = 0;
        var close = -1;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) { close = i; break; }
        }
        Assert.True(close > open, "ProjectConfig's body is unbalanced; the guard cannot bound it.");

        var projected = Regex.Matches(source[open..close], @"(\w+)\s*=\s*config\.")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(projected.Count >= 40,
            $"ProjectConfig now writes only {projected.Count} statics. It wrote fifty-one when "
          + "this guard was written; a much smaller number means the shape moved and this file is "
          + "describing a hazard that has relocated rather than closed.");

        // And the one that started it is still in the set, so the story above stays checkable.
        Assert.Contains("EnableSpecialistAntExecution", projected);
    }
}
