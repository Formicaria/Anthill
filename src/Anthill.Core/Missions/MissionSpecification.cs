namespace Anthill.Core.Missions;

/// <summary>What the operator wants DONE. One dimension of classification, never the whole label.</summary>
public enum MissionIntent
{
    /// <summary>Answer from what is already known. No inspection required.</summary>
    Explain,
    /// <summary>Establish current state by inspecting it, and report. Read-only.</summary>
    Assess,
    /// <summary>Determine the cause of an observed symptom. Beyond v0.3.8.98 — see ADR-008.</summary>
    Diagnose,
    /// <summary>Change something. Consequential, and gated as such.</summary>
    Change,
    /// <summary>
    /// Go and FIND OUT, from outside the colony, and come back with what was found and where it came
    /// from. v0.3.8.109.
    ///
    /// Distinct from <see cref="Assess"/>, which inspects something the colony can reach and holds
    /// the receipts for, and from <see cref="Explain"/>, which answers from what is already known.
    /// The separating property is not the question's difficulty but where the answer's evidence
    /// lives: an assessment's evidence is something the colony DID, and research's evidence is
    /// something the WORLD said — which is the whole reason its answers need citations and an
    /// assessment's do not.
    /// </summary>
    Research,
}

/// <summary>What the mission is ABOUT. A mission may name more than one.</summary>
[Flags]
public enum MissionTargets
{
    None = 0,
    /// <summary>The source tree: what is implemented.</summary>
    Repository = 1,
    /// <summary>Persisted and live records: what is enabled, what ran, what evidence exists.</summary>
    Runtime = 2,
    Project = 4,
    Service = 8,
    /// <summary>Something OUTSIDE the colony and outside the operator's own infrastructure — a
    /// webhook, a third-party endpoint, a channel other people read. v0.3.8.103. Distinct from
    /// <see cref="Service"/> because the consequence is: a service action is the operator's own
    /// machine and reverses, and a message that reached other people does not.</summary>
    External = 16,
    /// <summary>
    /// The world outside the colony as a SOURCE. v0.3.8.109.
    ///
    /// The boundary with <see cref="External"/> is direction, and it is the sharpest line in this
    /// enum because the two share a noun. External is where something GOES — a destination a human
    /// approves and an irreversible send lands on. World is where knowledge COMES FROM — pages the
    /// colony reads and must then be able to cite. "Post the summary to the team's webhook" and
    /// "look up what the vendor's changelog says" both name the outside world, and one of them can
    /// be undone by nobody.
    ///
    /// Nothing here is inspectable in the sense the other targets are: the colony cannot re-run the
    /// internet, which is why a mission aimed only at this target can never be a troubleshooting
    /// mission however it is worded, and why its evidence kind is a retrieval rather than a check.
    /// </summary>
    World = 32,
}

/// <summary>How recent the answer must be. "What did we do" and "what is true now" differ.</summary>
public enum MissionFreshness { Historical, Current, Live }

/// <summary>
/// The ceiling on what the mission may DO, agreed across specification, operator policy, worker
/// contract and adapter before dispatch. Ordered: each level includes the ones before it.
/// </summary>
public enum MissionAuthority { Observe, ExecuteChecks, Modify }

/// <summary>
/// One thing the operator asked for, with an identity that survives the whole mission.
///
/// The identity is the point. A deliverable that exists only as a clause inside the goal string is
/// one the runtime cannot be held to: the planner reads the goal one way, the assembler another,
/// and nothing can state afterwards whether the thing was produced. A stable id lets a task claim
/// to serve it, evidence attach to it, and the answer-coverage gate check it.
/// </summary>
/// <param name="Id">Stable within the mission. Derived from position, not from wording, so
/// rephrasing the request does not renumber what it asked for.</param>
/// <param name="Request">The operator's own words for this deliverable, not a paraphrase.</param>
/// <param name="Subject">The topic keywords a coverage check can look for in an answer.</param>
public sealed record MissionDeliverable(string Id, string Request, IReadOnlyList<string> Subject);

/// <summary>
/// THE AUTHORITATIVE ACCOUNT OF WHAT THE OPERATOR ASKED FOR. v0.3.8.98.
///
/// WHY IT EXISTS. Until this release the operator's request travelled as a string, and every layer
/// re-interpreted it: the planner read it to choose roles, `ObjectiveVerification` re-read it to
/// guess a deliverable, `ResultAssembler` never read it at all and returned the last builder task's
/// output as the answer. Three readings, no shared account, and therefore no layer able to state
/// whether the mission produced what was asked. Mission
/// `7afd85b2-e4a2-47ef-aa01-e5fa72ff00ca` is what that costs: two tasks completed, no evidence, the
/// requested assessment absent, and the mission presented as finished.
///
/// It is deliberately the same shape as <see cref="Domain.MissionConstraints"/>, which has been
/// parsed once at intake and carried on <c>MissionContext</c> since ADR-002 — that pattern already
/// works here, and adding a second one would be the drift this type exists to remove.
///
/// CLASSIFICATION IS MULTI-DIMENSIONAL, and that is not decoration. One label cannot separate
/// "what is implemented" from "what is running now" from "why is it broken": the first two are the
/// same read-only assessment against different targets, and the third is a different mission class
/// entirely. Collapsing them into a keyword is how an audit becomes a troubleshooting run, or how
/// "is the colony healthy?" gets answered from source code. See ADR-008 for the permanent boundary.
///
/// WHAT THIS TYPE IS NOT. It is not a plan, and it holds no tasks: it says what must be true when
/// the mission ends, never how to get there. It is not a model's opinion — a model may propose one,
/// and deterministic validation decides what is kept. And it is not complete at v0.3.8.98: only the
/// system-audit class is derived with real fidelity. Every other request resolves to a permissive
/// specification that changes nothing, which is how this ships without altering behaviour it has
/// not yet earned the right to alter.
/// </summary>
public sealed record MissionSpecification
{
    /// <summary>The operator's ask, verbatim — never the composed goal with its transcript.</summary>
    public required string OriginalRequest { get; init; }

    /// <summary>
    /// The mission class, from the closed set this release knows. `system_audit` is the only class
    /// with real machinery behind it at v0.3.8.98; `general` is the honest name for "not yet
    /// classified", and behaves exactly as the colony did before this type existed.
    /// </summary>
    public required string MissionClass { get; init; }

    public MissionIntent Intent { get; init; } = MissionIntent.Explain;
    public MissionTargets Targets { get; init; } = MissionTargets.None;
    public MissionFreshness Freshness { get; init; } = MissionFreshness.Current;
    public MissionAuthority Authority { get; init; } = MissionAuthority.Observe;

    /// <summary>What the operator asked to receive. Empty for an unclassified request.</summary>
    public IReadOnlyList<MissionDeliverable> Deliverables { get; init; } = Array.Empty<MissionDeliverable>();

    /// <summary>
    /// What the mission must be ABLE to do, as capability ids workers declare against. This is the
    /// input to worker resolution — the replacement for asking whether the task text happens to
    /// contain a word.
    /// </summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Evidence kinds a mission of this class must produce before its conclusions can be believed.
    /// An audit that inspected nothing has asserted rather than established, whatever it says.
    /// </summary>
    public IReadOnlyList<string> RequiredEvidence { get; init; } = Array.Empty<string>();

    /// <summary>The class name for a request this release does not classify.</summary>
    public const string GeneralClass = "general";

    /// <summary>The read-only assessment class v0.3.8.98 implements end to end.</summary>
    public const string SystemAuditClass = "system_audit";

    /// <summary>
    /// The diagnostic class v0.3.8.101 implements end to end: a reported symptom, reproduced by
    /// executed checks and explained by a diagnosis that cites their receipts. The first class to
    /// carry <see cref="MissionAuthority.ExecuteChecks"/> — and the boundary with the audit class
    /// is ADR-008's: an assessment that executed checks has left assessment, and a diagnosis that
    /// executed nothing has not diagnosed.
    /// </summary>
    public const string TroubleshootingClass = "troubleshooting";

    /// <summary>
    /// The infrastructure-action class v0.3.8.102 implements end to end: a change to a SERVICE
    /// target, reached through the homelab's own approval-gated pipeline and recorded as a
    /// reversible operation — before-state, receipt, after-state, rollback note, and a distinct
    /// human approval. The first class to carry <see cref="MissionAuthority.Modify"/>, and Modify
    /// still does not mean autonomy: the model proposes, the operator's recorded escalation
    /// decision executes, and every executor gate stands underneath (ADR-008: "every existing
    /// approval gate is preserved").
    /// </summary>
    public const string SystemActionClass = "system_action";

    /// <summary>
    /// The outbound class v0.3.8.103 implements end to end: something LEAVES the colony, to a
    /// destination resolved before a human approved it and recorded beside where the send actually
    /// landed. The second class to carry <see cref="MissionAuthority.Modify"/>, and the first whose
    /// authority ceiling is READ at dispatch rather than merely declared.
    ///
    /// The boundary with <see cref="SystemActionClass"/> is the VERB, not the noun: "notify the
    /// team's webhook that the container restarted" does nothing to the container. A resolver that
    /// let the service noun win would turn a notification into an infrastructure action, which is
    /// the worst direction for that to be wrong in.
    /// </summary>
    public const string ExternalActionClass = "external_action";

    /// <summary>
    /// The outward-reading class v0.3.8.109 implements end to end: a question the colony cannot
    /// answer from itself, answered from sources it retrieved and can name. Open since `.99`, when
    /// <c>CitationIntegrity</c> was built for a class that did not exist.
    ///
    /// IT CARRIES <see cref="MissionAuthority.Observe"/>, the same ceiling as the audit class, and
    /// the equality is worth stating because this class touches the network and that one does not.
    /// Observe is a ceiling on what the mission may CHANGE, and research changes nothing: it reads
    /// pages. The outbound network call is the web ant's own permission contract, which predates
    /// every class in this list and is unchanged by any of them.
    ///
    /// THE BOUNDARY WITH <see cref="SystemAuditClass"/> is the target, not the verb. "Assess the
    /// colony's retry policy" and "find out what the upstream project's retry policy is" are the
    /// same question asked of two different worlds, and the receipts differ absolutely: the first
    /// rests on an inspection the colony performed, the second on a page somebody else wrote. An
    /// audit answered from the internet is the failure this separation prevents.
    /// </summary>
    public const string ResearchClass = "research";

    /// <summary>
    /// True when this specification carries enough to hold the mission to something. A `general`
    /// specification is a record that intake ran and found no class it could serve honestly — the
    /// downstream layers read this rather than testing the class name, so adding a class later
    /// does not mean finding every `== "general"` in the codebase.
    /// </summary>
    public bool IsActionable => MissionClass != GeneralClass && Deliverables.Count > 0;

    /// <summary>Operator-visible projection, for the mission record and events. Secret-free.</summary>
    public Dictionary<string, object?> Snapshot() => new()
    {
        ["mission_class"] = MissionClass,
        ["intent"] = Intent.ToString().ToLowerInvariant(),
        ["targets"] = Targets.ToString().ToLowerInvariant(),
        ["freshness"] = Freshness.ToString().ToLowerInvariant(),
        ["authority"] = Authority.ToString().ToLowerInvariant(),
        ["deliverables"] = Deliverables.Select(d => new Dictionary<string, object?>
        {
            ["id"] = d.Id, ["request"] = d.Request,
        }).ToList(),
        ["required_capabilities"] = RequiredCapabilities,
        ["required_evidence"] = RequiredEvidence,
    };

    /// <summary>The permissive specification: intake ran, no class applied, nothing constrained.</summary>
    public static MissionSpecification General(string request) => new()
    {
        OriginalRequest = request ?? "",
        MissionClass = GeneralClass,
    };
}
