using System.Text.RegularExpressions;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// THE MICROMOUND CONSOLE OFFERS THE PROTOCOL'S VOCABULARY, NOT ITS OWN. v0.3.8.115.
///
/// `src/Anthill.UI/micromound.js` builds forms for charters, manifests and physical missions. Four
/// of those forms are driven by CLOSED sets — step operations, condition operators, worker runtime
/// types, offline behaviours, reasoning modes — and every one of them is declared in
/// `Micromound.Protocol`, the assembly both sides share.
///
/// WHY THIS GUARD, AND WHY HERE. `.114` named a defect class for a wire contract invented from the
/// spec instead of read from the client: enrolment was impossible through the front door for a whole
/// release because the shape was written from PROTOCOL.md, and nothing noticed because both ends of
/// every test were ours. A console form is the same failure one layer up — a dropdown listing an op
/// the device does not implement produces a mission the mound refuses, and the operator has no way
/// to tell that from a broken device.
///
/// This lives in `Anthill.Tests.Micromound` rather than beside the other console guards because
/// THIS is where the protocol types exist. `docs/GUARDS.md` puts a typed registry above a source
/// scan for exactly this reason: the sets below are read out of the compiled protocol assembly, so
/// adding an operation to the protocol fails this test rather than quietly leaving the console a
/// version behind.
/// </summary>
public class ConsoleVocabularyTests
{
    /// <summary>The ANTHILL checkout — the sibling of the micromound one this project also reads.</summary>
    private static string AnthillRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;

        Assert.True(dir is not null,
            "walked up from the test output directory and never found Anthill.sln, so this guard "
          + "cannot read the console it is about. A guard that cannot find its subject must fail, "
          + "not shrug — see docs/GUARDS.md.");
        return dir!.FullName;
    }

    private static string ConsoleSource()
    {
        var path = Path.Combine(AnthillRoot(), "src", "Anthill.UI", "micromound.js");
        Assert.True(File.Exists(path), $"the Micromound console asset is missing at '{path}'.");

        var text = File.ReadAllText(path);
        Assert.True(text.Length > 2_000,
            $"micromound.js is {text.Length} characters. Every assertion below would pass vacuously.");
        return text;
    }

    /// <summary>The JS array literal assigned to `name`, as a set of its string members.</summary>
    private static HashSet<string> JsConst(string source, string name)
    {
        var m = Regex.Match(source, @"const\s+" + Regex.Escape(name) + @"\s*=\s*\[(?<body>[^\]]*)\]");
        Assert.True(m.Success, $"micromound.js no longer declares `{name}` as an array literal.");

        var members = Regex.Matches(m.Groups["body"].Value, @"'([^']*)'")
            .Select(x => x.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(members);
        return members;
    }

    private static void AssertSameSet(string name, IReadOnlySet<string> protocolSet, HashSet<string> console)
    {
        var missing = protocolSet.Except(console, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var extra = console.Except(protocolSet, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            $"the protocol declares {string.Join(", ", missing)} and the console's `{name}` does not "
          + "offer it, so an operator cannot compose work the device can actually do.");

        Assert.True(extra.Count == 0,
            $"the console's `{name}` offers {string.Join(", ", extra)} and the protocol has no such "
          + "value. A form that accepts it produces a packet the mound refuses, which an operator "
          + "reads as a broken device.");
    }

    [Fact]
    public void TheStepOperations_AreTheProtocolsOwn() =>
        AssertSameSet("MM_STEP_OPS", MissionStepOps.All, JsConst(ConsoleSource(), "MM_STEP_OPS"));

    [Fact]
    public void TheConditionOperators_AreTheProtocolsOwn() =>
        AssertSameSet("MM_COND_OPS", ConditionOps.All, JsConst(ConsoleSource(), "MM_COND_OPS"));

    [Fact]
    public void TheWorkerRuntimeTypes_AreTheProtocolsOwn() =>
        AssertSameSet("MM_RUNTIME_TYPES", RuntimeTypes.All, JsConst(ConsoleSource(), "MM_RUNTIME_TYPES"));

    [Fact]
    public void TheOfflineBehaviours_AreTheProtocolsOwn() =>
        AssertSameSet("MM_OFFLINE", OfflineBehaviours.All, JsConst(ConsoleSource(), "MM_OFFLINE"));

    [Fact]
    public void TheReasoningModes_AreTheProtocolsOwn() =>
        AssertSameSet("MM_REASONING", ReasoningModes.All, JsConst(ConsoleSource(), "MM_REASONING"));

    /// <summary>
    /// THE ONE SET THAT IS DELIBERATELY NARROWER THAN THE PROTOCOL'S.
    ///
    /// `ActionClass` has four values and the console offers three. `hazardous` is real, and
    /// `MicromoundCharters.Issue` refuses any charter that asks for it — so a dropdown offering it
    /// would be a control whose only possible outcome is a refusal. This asserts the narrowing is
    /// exactly that one value and is not drift: if the issuer ever stops refusing hazardous, or the
    /// console starts offering it, one of the two assertions below fails.
    /// </summary>
    [Fact]
    public void TheActionCeilings_StopAtControlled_BecauseTheIssuerRefusesHazardous()
    {
        var console = JsConst(ConsoleSource(), "MM_CEILINGS");

        Assert.Equal(new[] { "benign", "controlled", "observe" },
            console.OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // The protocol still HAS it — this is a policy narrowing, not a stale copy of the enum.
        Assert.True(ActionClasses.TryParse("hazardous", out _),
            "the protocol no longer knows `hazardous`, so the console's narrowing is now describing "
          + "something that does not exist. Re-derive MM_CEILINGS.");

        // And the refusal that justifies the narrowing is still in force.
        var refusals = MicromoundCharterRefusesHazardous();
        Assert.True(refusals,
            "MicromoundCharters no longer refuses a hazardous ceiling, so the console is now hiding "
          + "an option an operator could legitimately grant. Either restore the refusal or widen "
          + "MM_CEILINGS deliberately.");
    }

    /// <summary>Reads the issuer's own source for the refusal the narrowing above depends on.</summary>
    private static bool MicromoundCharterRefusesHazardous()
    {
        var path = Path.Combine(AnthillRoot(), "src", "Anthill.Modules",
            "Anthill.Modules.Micromound", "Micromound", "MicromoundCharters.cs");
        Assert.True(File.Exists(path), $"MicromoundCharters.cs is missing at '{path}'.");
        return File.ReadAllText(path).Contains("\"hazardous\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// The mission form sends the field names the protocol reads.
    ///
    /// Asserted against the JSON names on `MissionStep` itself rather than against a list written
    /// here, so a rename in the protocol fails this instead of silently producing steps the device
    /// deserializes into defaults — which is the quiet version of `.114`'s enrolment bug: a request
    /// that parses, means nothing, and reports success.
    /// </summary>
    [Fact]
    public void TheMissionForm_SendsTheProtocolsFieldNames()
    {
        var source = ConsoleSource();

        foreach (var property in typeof(MissionStep).GetProperties())
        {
            var wire = property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
                .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
                .FirstOrDefault()?.Name;
            if (wire is null) continue;

            Assert.True(source.Contains(wire, StringComparison.Ordinal),
                $"MissionStep carries the wire field `{wire}` and micromound.js never mentions it. "
              + "Either the form cannot express that part of a step, or the protocol renamed it and "
              + "the console is still sending the old name.");
        }

        // The condition sub-object too — the one nested shape the form composes.
        foreach (var wire in new[] { "source_step", "op", "value" })
            Assert.Contains(wire, source);
    }
}
