using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// What a settings reset PRESERVES, and the list it tells the operator it preserved. v0.3.8.90.
///
/// WHY THIS EXISTS. `AnthillRuntime.ResetConfig` returns tunables to their defaults and is documented
/// to keep "connection settings … so the reset never disconnects or hides the colony". That rule is
/// implemented TWICE — once as an object initializer copying fields off the old config, and once as a
/// hand-written list of key names returned to the console. Two implementations of one rule is a named
/// defect class in this repository, and this pair had already drifted: the priority route
/// (`model_priority_provider` / `model_priority_model`, added in v3.8.1) was in neither, so a reset
/// silently discarded the operator's answer to "which model do I actually want".
///
/// v0.3.8.90 adds the price table, which is the same kind of value and a worse thing to lose: typed-in
/// reference data this process cannot rediscover, whose absence turns every later cost report back
/// into a gap with no indication that anything was dropped.
///
/// READ FROM SOURCE, deliberately. Calling `ResetConfig` would mutate the process-wide `Config` and
/// write `config.json` to disk — a test that reformats the developer's own configuration to check a
/// list is a bad trade. The property this needs is structural, and the structure is in the file.
/// </summary>
public class ConfigResetTests
{
    private static string RuntimeFile() => Path.Combine(SourceText.RepoRoot(),
        "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs");

    /// <summary>C# property name → the key an operator writes in `config.json`.</summary>
    private static Dictionary<string, string> JsonNames() =>
        typeof(AnthillConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .ToDictionary(p => p.Name, p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name,
                StringComparer.Ordinal);

    private static string ResetBody()
    {
        var code = SourceText.CodeOnly(File.ReadAllText(RuntimeFile()));
        var start = code.IndexOf("public static List<string> ResetConfig()", StringComparison.Ordinal);
        Assert.True(start >= 0, "ResetConfig has moved or been renamed; this guard reads it by name.");

        var end = code.IndexOf("public static void SaveConfig", start, StringComparison.Ordinal);
        Assert.True(end > start, "ResetConfig's end is no longer where this guard expects.");

        return code[start..end];
    }

    /// <summary>
    /// THE ASSERTION. Everything the initializer carries over is named in what the method returns.
    ///
    /// Both directions matter and they are different failures. A field preserved but not reported is
    /// a reset that quietly did less than it said; a key reported but not preserved is a reset that
    /// told the operator their value survived when it did not — and the second is how an operator
    /// stops trusting the console.
    /// </summary>
    [Fact]
    public void EverythingResetPreserves_IsAlsoWhatItReportsPreserving()
    {
        var body = ResetBody();
        var names = JsonNames();

        var preserved = Regex.Matches(body, @"(?<prop>[A-Za-z]\w*)\s*=\s*old\.\k<prop>\b")
            .Select(m => m.Groups["prop"].Value)
            .Distinct(StringComparer.Ordinal)
            .Select(prop => names.GetValueOrDefault(prop, $"<no json name for {prop}>"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(preserved.Count >= 8,
            $"this guard found only {preserved.Count} preserved field(s) in ResetConfig; the shape it "
          + "reads (`X = old.X`) has changed and it would pass over almost nothing.");

        var reported = Regex.Matches(
                body[body.IndexOf("return new List<string>", StringComparison.Ordinal)..],
                @"""(?<key>[a-z][a-z0-9_]*)""")
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var silentlyKept = preserved.Except(reported).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var falselyClaimed = reported.Except(preserved).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(silentlyKept.Count == 0,
            "ResetConfig preserves these settings and does not report doing so: "
          + string.Join(", ", silentlyKept)
          + ". The returned list is what the console shows the operator, so a reset that kept more "
          + "than it admits is a reset nobody can reason about.");

        Assert.True(falselyClaimed.Count == 0,
            "ResetConfig reports preserving these and the initializer does not carry them: "
          + string.Join(", ", falselyClaimed)
          + ". The operator is told their value survived a reset that discarded it.");
    }

    /// <summary>
    /// The two settings that had drifted out, pinned by name so the regression is legible.
    ///
    /// Neither is a tunable with a safe default. Both are things only the operator knows: which model
    /// they actually want, and what their provider charges. A reset restores defaults; it does not
    /// get to forget facts.
    /// </summary>
    [Fact]
    public void TheOperatorsOwnValues_SurviveAReset()
    {
        var body = ResetBody();

        foreach (var property in new[]
                 {
                     "ModelPriorityProvider", "ModelPriorityModel",
                     "ModelPricing", "ModelPricingCurrency",
                 })
            Assert.True(Regex.IsMatch(body, $@"{property}\s*=\s*old\.{property}\b"),
                $"ResetConfig no longer preserves {property}. That value is not a tunable with a "
              + "sensible default — it is something the operator typed in and this process cannot "
              + "rediscover, so a reset that drops it destroys information rather than restoring it.");
    }
}
