using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Every field `/status` computes has a consumer. v3.8.34.
///
/// THE DEFECT THIS GENERALISES
/// ---------------------------
/// `/status` has returned `ollama_model_present` since v2.4.3, added for one specific symptom, with
/// a comment saying exactly why:
///
/// > `/api/version` alone lied by omission — Ollama can be up while the configured model is absent,
/// > and every ant call then fails although the chip showed green.
///
/// `app.js` contained ZERO references to it. The server computed the answer, serialised it, sent it
/// over the wire, and the console dropped it — so the chip stayed green while every mission failed,
/// which is the exact state the field was introduced to prevent. It took an operator debugging a
/// live Ollama box to find it.
///
/// That is the ninth "implemented, tested, and unreachable" in this repository and the first in the
/// UI. The eight before it were all backend, and the call-site audit that catches those does not
/// read JavaScript.
///
/// WHY A FIELD RATHER THAN A FUNCTION
/// ----------------------------------
/// A status field is a claim the backend makes about itself. Computing one costs a probe, a
/// timeout and a round trip; if nothing reads it, all of that is spent to produce a fact nobody
/// sees. Worse, its EXISTENCE reassures a reader that the case is handled — which is why the bug
/// survived: anyone auditing "does the console know the model might be missing?" would have found
/// the field and stopped there.
/// </summary>
public class StatusFieldConsumerTests
{
    private static string Root() => SourceText.RepoRoot();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Fields the console legitimately does not read, each with the reason it is exempt.
    ///
    /// An allow-list rather than a looser rule, because every entry should cost someone a decision.
    /// "Nothing reads it" is a finding; "nothing reads it AND here is why that is fine" is a design.
    /// </summary>
    private static readonly Dictionary<string, string> NotConsumedByTheConsole = new()
    {
        ["routes"] = "rendered by the model-routing tab from /settings, not from /status",
        ["local_role_count"] = "diagnostic; superseded by routing_mode in the console",
        ["provider_role_count"] = "diagnostic; superseded by routing_mode in the console",
        ["configured_model"] = "v3.8.33: reported so an operator can tell a chosen model from a "
                             + "resolved one; the console shows the resolved value and the reason",
        ["installed_models"] = "v3.8.33: the picker reads the richer /ollama/models instead",
        ["model_resolved"] = "v3.8.33: read by the console — pinned here only if that changes",
    };

    /// <summary>The keys `/status` actually emits, read from the source that emits them.</summary>
    private static List<string> StatusFields()
    {
        var source = SourceText.CodeOnly(Read("src/Anthill.Api/ApiHost.Reports.cs"));

        // The dictionary literal that becomes the /status payload.
        var start = source.IndexOf("[\"version\"] = AnthillRuntime.Version", StringComparison.Ordinal);
        Assert.True(start >= 0, "The /status payload is no longer recognisable — this guard needs its new shape.");

        var end = source.IndexOf("};", start, StringComparison.Ordinal);
        var block = source[start..(end < 0 ? source.Length : end)];

        return Regex.Matches(block, @"\[""(?<key>[a-z0-9_]+)""\]\s*=")
            .Select(m => m.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The guard. A computed field with no reader is work spent to produce nothing.</summary>
    [Fact]
    public void EveryStatusField_IsEitherReadByTheConsoleOrExplicitlyExempt()
    {
        var console = Read("src/Anthill.UI/app.js") + Read("src/Anthill.UI/index.html")
                    + Read("src/Anthill.UI/dashboard-grid.js") + Read("src/Anthill.UI/mission-thread.js");

        var orphans = StatusFields()
            .Where(f => !NotConsumedByTheConsole.ContainsKey(f))
            .Where(f => !console.Contains(f, StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphans.Count == 0,
            "/status computes these fields and NOTHING in the console reads them. That is how "
            + "`ollama_model_present` came to be probed on every request since v2.4.3 while the "
            + "status chip stayed green through a completely unusable colony. Either wire them up, "
            + "or add them to NotConsumedByTheConsole with the reason: " + string.Join(", ", orphans));
    }

    /// <summary>
    /// The exemption list may not name fields that no longer exist. A stale exemption silently
    /// re-opens the hole it was written for — and reads as a decision someone made about today.
    /// </summary>
    [Fact]
    public void TheExemptionList_NamesOnlyFieldsThatStillExist()
    {
        var fields = StatusFields();
        var stale = NotConsumedByTheConsole.Keys
            .Where(k => !fields.Contains(k, StringComparer.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These fields are exempted from the consumer guard but /status no longer emits them: "
            + string.Join(", ", stale));
    }

    /// <summary>
    /// The specific regression, pinned by name. The theory above would still pass if
    /// `ollama_model_present` were deleted outright, and deleting it is the WRONG fix — the field is
    /// correct, it was the console that ignored it.
    /// </summary>
    [Fact]
    public void TheModelPresenceField_IsStillComputedAndStillRead()
    {
        Assert.Contains("ollama_model_present", Read("src/Anthill.Api/ApiHost.Reports.cs"), StringComparison.Ordinal);
        Assert.Contains("ollama_model_present", Read("src/Anthill.UI/app.js"), StringComparison.Ordinal);
    }

    /// <summary>
    /// ...and the console must act on it, not merely mention it. A field referenced once in a
    /// variable nobody branches on would satisfy the guard above while changing nothing on screen —
    /// which is the shape of the original bug, one level down.
    /// </summary>
    [Fact]
    public void TheConsole_ChangesItsStatusWhenTheModelIsUnusable()
    {
        var js = Read("src/Anthill.UI/app.js");

        Assert.Contains("modelUnusable", js, StringComparison.Ordinal);   // the attention banner
        Assert.Contains("modelBad", js, StringComparison.Ordinal);        // the status chip
        Assert.Contains("model_choice_reason", js, StringComparison.Ordinal); // the operator-facing why
    }
}
