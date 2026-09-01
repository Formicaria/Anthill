using System.Text.Json;
using System.Text.Json.Serialization;
using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Missions;

/// <summary>
/// WHAT THE OPERATOR ASKED FOR, WRITTEN DOWN ONCE AND NEVER DERIVED AGAIN. v0.3.8.104.
///
/// WHAT WAS WRONG, and it is the defect this whole release exists to close. `MissionContext.Create`
/// called `MissionIntake.Resolve(mission.Goal)` on every context it built, and three further sites
/// re-derived meaning from the same string independently. Nothing was persisted. So a mission's
/// class, deliverables, authority and constraints were not facts about the mission — they were
/// facts about whatever the intake rules happened to say the moment somebody asked.
///
/// `.103` proved why that matters rather than merely being untidy: it added a class and four verbs
/// to intake. A `.102` mission replayed under `.103` therefore reclassifies, and `.98` wrote the
/// rule this breaks in its own words — a grade has to be reproducible from the persisted record. It
/// was not. Every evaluation this colony has ever stored was computed against intake rules that
/// have since moved, and nothing could say so.
///
/// SO THE CONTRACT IS WRITTEN AT INTAKE AND READ FOREVER AFTER. It carries everything needed to
/// reproduce the original classification WITHOUT rerunning the current rules: the specification
/// (class, intent, targets, freshness, authority, deliverables, required capabilities, required
/// evidence), the parsed constraints, the verification policy in force, and the version of the
/// intake ruleset that produced it. A later release may classify differently; a mission written
/// under this one keeps what it was admitted as.
///
/// IT IS WRITE-ONCE, and that is enforced by where it lives rather than by discipline. A separate
/// table, not columns on `missions`, because `SaveMission` is an `INSERT OR REPLACE` — the
/// evaluation columns already learned this the hard way and have to be written by a later `UPDATE`
/// to survive. A contract that could be erased mid-mission by an unrelated save would be a contract
/// in name only.
///
/// <see cref="IntakeVersion"/> IS NOT DECORATION. It is how a reader tells "this is what intake
/// decided then" from "this is what intake would decide now" — the second being what every mission
/// before this release has, and what <see cref="LegacyIntakeVersion"/> marks. Flattening the two
/// would reintroduce the defect one layer down: a record that cannot say whether it is a record.
/// </summary>
public sealed record MissionContract
{
    /// <summary>The DOCUMENT's schema, so a future reader can migrate a stored contract rather than
    /// guess at it. Distinct from <see cref="IntakeVersion"/>, which is about the rules that
    /// produced the content, not the shape that holds it.</summary>
    [JsonPropertyName("contract_version")] public int ContractVersion { get; init; } = CurrentContractVersion;

    /// <summary>The release whose intake rules produced this. <see cref="LegacyIntakeVersion"/> for
    /// a mission that predates contracts and was resolved when it was read.</summary>
    [JsonPropertyName("intake_version")] public required string IntakeVersion { get; init; }

    /// <summary>The operator's ask, verbatim — never the composed goal with its transcript.</summary>
    [JsonPropertyName("original_request")] public required string OriginalRequest { get; init; }

    [JsonPropertyName("specification")] public required MissionSpecification Specification { get; init; }

    /// <summary>Parsed once here, for the same reason the specification is. ADR-002 established
    /// this for constraints and the codebase then re-parsed them downstream anyway.</summary>
    [JsonPropertyName("constraints")] public required MissionConstraints Constraints { get; init; }

    /// <summary>
    /// Whether this mission's class is objectively verified, decided at intake and recorded so a
    /// replay cannot silently apply a different policy. From `.104` a recognized class is always
    /// verified and does not consult the operator switch; the flag's value is recorded anyway,
    /// because "this ran while the switch was off" is a fact about the run.
    /// </summary>
    [JsonPropertyName("verification_required")] public required bool VerificationRequired { get; init; }

    [JsonPropertyName("objective_verification_flag")] public required bool ObjectiveVerificationFlag { get; init; }

    /// <summary>True when this contract was RESOLVED AT READ rather than recorded at intake — a
    /// mission older than `.104`. Never silently equivalent to a real one.</summary>
    public bool IsLegacy =>
        string.Equals(IntakeVersion, LegacyIntakeVersion, StringComparison.Ordinal);

    public const int CurrentContractVersion = 1;

    /// <summary>The intake ruleset that is current in THIS build — the running version. A mission
    /// admitted today records the release whose rules admitted it, which is the only value that
    /// lets a later reader say whether the rules have moved since.</summary>
    public static string CurrentIntakeVersion() => AnthillRuntime.Version;

    /// <summary>The marker for a mission that predates contracts. Its specification is what TODAY's
    /// rules say, not what the mission was admitted as, and every reader can tell.</summary>
    public const string LegacyIntakeVersion = "legacy:resolved-at-read";

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static MissionContract? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<MissionContract>(json!, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Operator-visible projection, for the mission record and events. Secret-free.</summary>
    public Dictionary<string, object?> Snapshot() => new()
    {
        ["contract_version"] = ContractVersion,
        ["intake_version"] = IntakeVersion,
        ["legacy"] = IsLegacy,
        ["verification_required"] = VerificationRequired,
        ["specification"] = Specification.Snapshot(),
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// THE ONE PLACE THE OPERATOR'S GOAL IS INTERPRETED. v0.3.8.104.
///
/// Every call to <see cref="MissionIntake.Resolve"/> and <see cref="MissionConstraints.Parse"/> in
/// production lives in this file and nowhere else, and `MissionContractCallSiteTests` asserts that
/// by reading the source. That is not tidiness: before this release those two functions had five
/// call sites between them, each re-deriving what the mission WAS from a string that had already
/// been interpreted — the planner, the adaptive controller, the medic's troubleshooting check, and
/// the coder's constraint check all asking the same question separately and able to disagree.
///
/// A guard rather than a convention, because the convention already existed. ADR-002 said
/// constraints are parsed once at intake, and `Ants.cs` parsed them again anyway for two years.
/// </summary>
public static class MissionContracts
{
    /// <summary>
    /// The mission's contract: the recorded one if it exists, otherwise one resolved now, recorded,
    /// and returned.
    ///
    /// A mission reaching this a second time — a resume, a restart, a replay — gets the FIRST
    /// answer, which is the entire point. The write happens once and later reads are reads.
    /// </summary>
    public static MissionContract LoadOrCreate(Memory.SqliteMemory memory, Domain.Mission mission)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(mission);

        var existing = memory.LoadMissionContract(mission.Id);
        if (existing is not null) return existing;

        var contract = Resolve(mission.Goal, MissionContract.CurrentIntakeVersion());
        memory.SaveMissionContract(mission.Id, contract);
        return contract;
    }

    /// <summary>
    /// A contract for something that is not a stored mission — the plan preview, tooling, a test.
    /// Resolved and NOT persisted, because there is no mission for it to be the contract of.
    /// </summary>
    public static MissionContract ForPreview(string? goal) =>
        Resolve(goal ?? "", MissionContract.CurrentIntakeVersion());

    /// <summary>
    /// A contract for a mission that predates contracts, resolved at read and marked as such. The
    /// specification is what TODAY's rules say — which is not what the mission was admitted as, and
    /// the marker is how every reader can tell the difference.
    /// </summary>
    public static MissionContract Legacy(string? goal) =>
        Resolve(goal ?? "", MissionContract.LegacyIntakeVersion);

    /// <summary>
    /// THE SINGLE INTERPRETATION SITE. Both derivations happen here, together, once.
    /// </summary>
    private static MissionContract Resolve(string goal, string intakeVersion)
    {
        var specification = MissionIntake.Resolve(goal);
        var constraints = MissionConstraints.Parse(goal);

        return new MissionContract
        {
            IntakeVersion = intakeVersion,
            OriginalRequest = specification.OriginalRequest,
            Specification = specification,
            Constraints = constraints,
            // A recognized class is verified whatever the operator switch says — see
            // `MissionEvaluator`. The switch's value is recorded beside the decision rather than
            // replaced by it, because "this ran while the switch was off" is a fact about the run
            // that an operator reading the record later will want.
            VerificationRequired = RecognizedClasses.Contains(specification.MissionClass),
            ObjectiveVerificationFlag = AnthillRuntime.EnableObjectiveVerification,
        };
    }

    /// <summary>
    /// The classes whose gates run unconditionally. Named once, here, because the evaluator asks
    /// the same question and two spellings of "recognized" would eventually disagree about which
    /// missions are graded.
    /// </summary>
    public static readonly IReadOnlySet<string> RecognizedClasses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MissionSpecification.SystemAuditClass,
            MissionSpecification.TroubleshootingClass,
            MissionSpecification.SystemActionClass,
            MissionSpecification.ExternalActionClass,
        };
}
