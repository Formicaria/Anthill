using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Readiness;

/// <summary>
/// v2.26.0 pre-V3 hardening — the machine-generated qualification report.
///
/// Written from MEASURED results only: the live readiness evaluation (measured thresholds +
/// recorded operator attestations) and the configuration-health findings. Neither configuration
/// nor a model can declare the installation qualified — a critical config finding (the
/// keep-without-verify break-glass above all) forces NOT QUALIFIED regardless of every other
/// gate, and the report says exactly why.
/// </summary>
public static class QualificationReportWriter
{
    public static (string JsonPath, string MarkdownPath) Write(
        ReadinessReport readiness, IReadOnlyList<ConfigFinding> configFindings, string? outputDir = null)
    {
        var dir = outputDir ?? AnthillRuntime.PathFromScript("data/reports");
        Directory.CreateDirectory(dir);

        var breakGlass = configFindings.Any(f => f.Severity == "critical");
        var qualified = readiness.Ready && !breakGlass;
        var reasons = new List<string>();
        if (!readiness.Ready)
            reasons.AddRange(readiness.Checks.Where(c => !c.Satisfied)
                .Select(c => $"threshold unsatisfied: {c.Id} — {c.Title}"));
        foreach (var f in configFindings.Where(f => f.Severity == "critical"))
            reasons.Add($"critical config finding: {f.Combination} — {f.Detail}");

        var generatedAt = AnthillTime.NowUtc().ToIso();
        var payload = new Dictionary<string, object?>
        {
            ["generated_at"] = generatedAt,
            ["anthill_version"] = AnthillRuntime.Version,
            ["schema_version"] = AnthillRuntime.SchemaVersion,
            ["qualified"] = qualified,
            ["verdict"] = qualified ? "QUALIFIED" : "NOT QUALIFIED",
            ["reasons"] = reasons,
            ["readiness_statement"] = readiness.Statement,
            ["thresholds"] = readiness.Checks.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id, ["title"] = c.Title, ["kind"] = c.Kind.ToString().ToLowerInvariant(),
                ["satisfied"] = c.Satisfied, ["measured_holds"] = c.MeasuredHolds,
                ["attested"] = c.Attested, ["detail"] = c.Detail,
            }).ToList(),
            ["config_findings"] = configFindings.Select(f => new Dictionary<string, object?>
            {
                ["severity"] = f.Severity, ["combination"] = f.Combination, ["detail"] = f.Detail,
            }).ToList(),
            ["break_glass_enabled"] = breakGlass,
        };

        var jsonPath = Path.Combine(dir, "v3-qualification.json");
        File.WriteAllText(jsonPath, Json.Dumps(payload, indented: true));

        var md = new List<string>
        {
            "# V3 Qualification Report",
            "",
            $"Generated {generatedAt} by ANTHILL v{AnthillRuntime.Version} (schema {AnthillRuntime.SchemaVersion})",
            "",
            $"## Verdict: {(qualified ? "QUALIFIED" : "NOT QUALIFIED")}",
            "",
            readiness.Statement,
            "",
        };
        if (reasons.Count > 0)
        {
            md.Add("## Blocking reasons");
            md.Add("");
            md.AddRange(reasons.Select(r => $"- {r}"));
            md.Add("");
        }
        md.Add("## Thresholds");
        md.Add("");
        foreach (var c in readiness.Checks)
            md.Add($"- [{(c.Satisfied ? "PASS" : "FAIL")}] **{c.Title}** ({c.Id}, {c.Kind}) — {c.Detail}");
        md.Add("");
        md.Add("## Configuration health");
        md.Add("");
        md.Add(configFindings.Count == 0 ? "- no findings" : "");
        md.AddRange(configFindings.Select(f => $"- [{f.Severity}] {f.Combination}: {f.Detail}"));
        md.Add("");
        md.Add("_This report is generated from measured results. It cannot be satisfied by silence, "
             + "configuration, or model output._");

        var mdPath = Path.Combine(dir, "v3-qualification.md");
        File.WriteAllText(mdPath, string.Join("\n", md));
        return (jsonPath, mdPath);
    }
}
