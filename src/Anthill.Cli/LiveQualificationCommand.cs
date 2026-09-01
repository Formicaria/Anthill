using System.Text.Json;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Missions;
using Anthill.Core.Readiness;
using Anthill.SDK.Artifacts;

namespace Anthill.Cli;

/// <summary>
/// EXPORT THE LIVE QUALIFICATION RECORD FOR A MISSION THAT ACTUALLY RAN. v0.3.8.104.
///
/// WHY THIS EXISTS, and it is the finding rather than the feature. `LiveQualificationRecord` has
/// been complete, tested and correct since `.89`. `QUALIFICATION.md` §3 has listed "an exported
/// `LiveQualificationRecord`" as an open exit-gate item since `.97`, carried forward through seven
/// releases. And `LiveQualificationRecord.For` had NO PRODUCTION CALLER — only tests. The item was
/// not open because the work was hard; it was open because nothing could produce one.
///
/// That is this repository's house defect in its purest form: a component that exists, works, is
/// covered by tests, and is unreachable from anything an operator can run. The same shape as `.98`'s
/// capability branch that compiled and never executed, the operator report compiler that had writers
/// and no reader for sixteen releases, and `manage_models` required by an endpoint and absent from
/// every permission table.
///
/// WHAT THIS COMMAND DOES NOT DO: run a live mission. It reads one that already ran, from the
/// operator's own store, and writes the record. The run needs a real provider and a real request,
/// which is the operator's step and always was — what was missing is the export, and now the export
/// is one command.
///
/// IT WRITES WHAT IT MEASURED AND SAYS WHAT IT DID NOT. `RecordedField.Measured` is false for every
/// field nothing in the runtime produces, and those are printed under their own heading rather than
/// rendered as zeroes. A qualification report that shows `0` for something nothing records satisfies
/// the table while telling the operator something false — the rule `LiveQualificationRecord` was
/// built around, kept here at the point of export.
/// </summary>
public static class LiveQualificationCommand
{
    /// <summary>
    /// Export the record for one mission, or list what is available when none is named.
    /// </summary>
    /// <param name="args">Mission id, then optionally `--json &lt;path&gt;`.</param>
    public static int Run(string[] args)
    {
        var missionId = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var jsonPath = JsonPathFrom(args);

        using var memory = new SqliteMemory(AnthillRuntime.DbPath);

        if (string.IsNullOrWhiteSpace(missionId))
        {
            ListCandidates(memory);
            return 1;
        }

        var mission = memory.GetMission(missionId!);
        if (mission is null)
        {
            Console.Error.WriteLine($"No mission '{missionId}' in this colony's store.");
            ListCandidates(memory);
            return 1;
        }

        var record = LiveQualificationRecord.For(memory, memory, memory, missionId!);
        var contract = memory.LoadMissionContract(missionId!);
        var evaluation = memory.LoadMissionEvaluation(missionId!);

        Console.WriteLine($"LIVE QUALIFICATION — mission {missionId}");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"  goal        : {Trim(mission.GetValueOrDefault("goal")?.ToString())}");
        Console.WriteLine($"  outcome     : {evaluation?.OutcomeCode ?? "(no persisted evaluation)"}");
        Console.WriteLine($"  explanation : {Trim(evaluation?.Explanation)}");

        // v0.3.8.104 — the contract, because a live record whose classification cannot be tied to a
        // recorded ruleset is a measurement of something the reader cannot identify later.
        Console.WriteLine(contract is null
            ? "  contract    : NONE — this mission predates recorded contracts, so its class is "
              + "what today's rules say rather than what it was admitted as"
            : $"  contract    : {contract.Specification.MissionClass} "
              + $"(intake {contract.IntakeVersion}{(contract.IsLegacy ? ", resolved at read" : "")}, "
              + $"authority {contract.Specification.Authority})");

        Console.WriteLine($"  duration    : {(record.MissionDurationMs is null ? "unmeasured" : record.MissionDurationMs + " ms")}");
        Console.WriteLine($"  replays     : {(record.Reconstructs ? "yes" : "no")}");
        foreach (var gap in record.ReconstructionGaps)
            Console.WriteLine($"                gap: {gap}");

        Console.WriteLine();
        Console.WriteLine("  MEASURED FIELDS");
        foreach (var field in record.Fields.Where(f => f.Measured))
            Console.WriteLine($"    {field.Field,-32} {field.Value}");

        // Printed as their own section, never as zeroes. See the type's own remarks.
        var unmeasured = record.Unmeasured;
        if (unmeasured.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  NOT MEASURED — nothing in the runtime produces these. Not zero:");
            foreach (var field in unmeasured)
                Console.WriteLine($"    {field.Field,-32} {field.Note}");
        }

        Console.WriteLine();
        Console.WriteLine("  ROLE TELEMETRY");
        foreach (var role in record.Roles)
            Console.WriteLine(
                $"    {role.Role,-18} trigger={role.Trigger,-12} calls={role.ModelCalls,-3} "
              + $"provider={role.Provider ?? "-"} model={role.Model ?? "-"} "
              + $"prompt={Show(role.PromptTokens)} completion={Show(role.CompletionTokens)}"
              + (role.FailureClass is null ? "" : $" failure={role.FailureClass}"));

        if (jsonPath is not null)
        {
            File.WriteAllText(jsonPath,
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine();
            Console.WriteLine($"  written to {jsonPath}");
        }

        Console.WriteLine();
        // The exit code answers the QUALIFICATION question, not the "did this command work" one:
        // a record with unmeasured fields is a successful export of an incomplete measurement, and
        // `QUALIFICATION.md`'s rule is that unmeasured is not qualified.
        var qualified = unmeasured.Count == 0 && record.Reconstructs;
        Console.WriteLine(qualified
            ? "QUALIFIED — every field this run needed was measured, and the mission replays."
            : "NOT FULLY QUALIFIED — the sections above name every field that was not measured and "
              + "every way the mission does not replay. Unmeasured is not qualified.");
        return qualified ? 0 : 1;
    }

    private static void ListCandidates(SqliteMemory memory)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: anthill --live-qualification <mission-id> [--json <path>]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Recent missions in this colony:");
        try
        {
            foreach (var row in memory.GetRecentMissions(10))
                Console.Error.WriteLine(
                    $"  {row.GetValueOrDefault("id")}  {row.GetValueOrDefault("outcome_code") ?? row.GetValueOrDefault("status")}"
                  + $"  {Trim(row.GetValueOrDefault("goal")?.ToString())}");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"  (could not list missions: {error.Message})");
        }
    }

    private static string? JsonPathFrom(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, "--json", StringComparison.Ordinal));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Show(int? value) => value?.ToString() ?? "-";

    private static string Trim(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "-" :
        text!.Length <= 90 ? text.Replace("\n", " ") : text[..90].Replace("\n", " ") + "…";
}
