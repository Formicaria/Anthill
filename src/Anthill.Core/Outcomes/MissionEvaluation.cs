using Anthill.Core.Common;
using Anthill.Core.Domain;

namespace Anthill.Core.Outcomes;

/// <summary>
/// v2.26.0 pre-V3 hardening — ONE mission outcome.
///
/// Before this, six call sites independently re-derived whether a mission succeeded
/// (Queen finalization ×2, the Director's row re-derivation, restored-mission listing, objective
/// verification, candidate promotion) — and they could disagree, because task rows lack fields the
/// live path uses, and one caller resolved the outcome MID-mission while status was still Running
/// (which is why v2.23's route registration never actually registered anything).
///
/// A mission is now evaluated exactly once, after every task is terminal, by
/// <see cref="MissionEvaluator.Evaluate"/>; the result is persisted BEFORE completion is published;
/// and every downstream positive path consumes the persisted record. The old helpers survive only
/// as internals of this evaluator (and as the adaptive controller's mid-mission *progress* probe,
/// which is explicitly not a mission-final authority).
/// </summary>
public sealed record MissionEvaluation(
    string MissionId,
    string OutcomeCode,           // MissionOutcome vocabulary — the closed set, never free text
    string StructuralStatus,      // MissionStatus.Value(): complete | partial | failed
    string VerificationStatus,    // MissionEvaluation.Verification.* — v0.3.8.140 split `failed`
                                  // ("something said no") from `inconclusive` ("nothing could say")
    string DeliverableStatus,     // MissionEvaluation.Deliverable.*
    string? StopReason,           // MissionStopReasons.* | null — see that type; adaptive_stop
                                  // and adaptive_stop_satisfied are NOT the same outcome
    string EvaluatorVersion,
    string EvaluatedAt,
    string Explanation)
{
    /// <summary>THE positive predicate. Everything that reinforces, credits, applies, or completes
    /// on success must ask this record — nothing may re-derive it.</summary>
    public bool IsPositive => OutcomeCode == MissionOutcome.CompletedVerified;

    public static class Verification
    {
        public const string Passed = "passed";

        /// <summary>
        /// SOMETHING SAID NO. v0.3.8.140 narrowed this to its literal meaning: a verdict-bearing
        /// task reached `Failed` or `NeedsImprovement`. It used to mean that AND every other way the
        /// gate could refuse, which is what made it undemotable — see <see cref="Inconclusive"/>.
        /// </summary>
        public const string Failed = "failed";

        /// <summary>
        /// NOTHING COULD SAY ANYTHING. v0.3.8.140, and it is the distinction `.122` needed and did
        /// not have.
        ///
        /// `docs/PLAN.md` §2e records why that release's closure reconciliation was reverted:
        /// "`Verification.Failed` does not mean 'a check said no' … `failed` spans 'the check said
        /// no' and 'nothing could satisfy the check'. Demoting on it reclassified a legitimately
        /// complete mission." A model-authored pass with no evidence behind it is downgraded to
        /// `Unknown`; an unreadable store is `Unavailable`; a verification step that never completed
        /// produced no ruling at all. None of those is a verdict, and treating them as one is how a
        /// mission that genuinely finished got called failed.
        ///
        /// So they land here instead. This is NOT a pass and never softens into one — it is exactly
        /// as unverified as <see cref="Failed"/> — but it does not demote the structural status,
        /// because nothing established that anything went wrong.
        /// </summary>
        public const string Inconclusive = "inconclusive";

        public const string NotRun = "not_run";
    }

    public static class Deliverable
    {
        public const string Satisfied = "satisfied";
        public const string NotSatisfied = "not_satisfied";
        /// <summary>The goal asks for no tangible deliverable (research/report missions).</summary>
        public const string NotApplicable = "not_applicable";
        /// <summary>Objective verification is disabled — the layer did not run. Distinct from
        /// NotApplicable so a disabled check can never masquerade as a passed one.</summary>
        public const string NotChecked = "not_checked";
    }
}

public static class MissionEvaluator
{
    /// <summary>Bumped whenever the evaluation rules change, so a persisted evaluation always says
    /// which rules produced it. "legacy" marks rows that predate persisted evaluation.</summary>
    /// <summary>
    /// v3.8.22 — bumped to v2. The deterministic-block layer can flip an outcome from
    /// completed_verified to completed_unverified, so an evaluation produced before it and one
    /// produced after it are not comparable, and a stored row must say which rules made it. That is
    /// the entire purpose of this constant and it had never been exercised: the generation-integrity
    /// layer in v3.0.1 was the same kind of change and left the version at v1, which means every row
    /// between v3.0.1 and here claims a rule set it was not evaluated under. Not retroactively
    /// fixable; noted so the next rules change does not repeat it.
    /// </summary>
    /// <summary>v3 (v0.3.8.66): the verification layer consumes the evidence store's identity
    /// testimony — a mission with a materialized patch requires deterministic evidence bound to
    /// its FINAL revision and tree, and task-pairing alone no longer promotes. A v2 row and a v3
    /// row are graded under different rules and must say so.</summary>
    public const string Version = "evaluator-v3";
    public const string LegacyVersion = "legacy";

    /// <summary>
    /// Evaluate a finished mission. Call exactly once, after every task is terminal and
    /// mission.Status is final. The three layers are computed independently and combined here —
    /// nowhere else:
    ///   structural (did the plan run) → verification (did a verifier PASS, verdict-gated) →
    ///   deliverable (did the goal's tangible ask actually get produced).
    /// `completed_verified` requires all three. A stop reason (timeout / cancel / adaptive
    /// escalation) overrides everything: an interrupted mission is never any flavour of completed.
    /// </summary>
    /// <param name="constraints">v3.1.0 (ADR-002): the mission's constraints, resolved once at
    /// intake. The evaluator must read a mission's own instructions from the same object the
    /// admission gate and the planner used, not from a ninth parse of the goal string.</param>
    /// <param name="objectiveVerificationEnabled">v3.1.0 (ADR-001): the run's verification policy,
    /// passed in rather than read from a mutable static. This keeps the evaluator a PURE function
    /// of its arguments — the property that makes "evaluated exactly once, and reproducibly" a
    /// checkable claim rather than an aspiration.</param>
    /// <param name="evidence">v0.3.8.66 (§2 item 2): the mission's evidence rows, so the
    /// verification layer can require identity for missions that materialized a patch. Null means
    /// the store could not be read; for a revision-bearing mission that fails closed.</param>
    /// <param name="specification">v0.3.8.98: what the operator ASKED FOR, resolved once at intake.
    /// The deliverable layer below could grade a file change and nothing else, so an assessment
    /// mission — which changes nothing by construction — collapsed onto a verifier model saying the
    /// right words. Null keeps the pre-v0.3.8.98 behaviour exactly.</param>
    /// <param name="consumptions">v0.3.8.98: the artifact consumption ledger, so "the verifier
    /// consumed nothing" is answerable from a record instead of assumed. Null fails the assessment
    /// objective closed, for the same reason a null evidence list does.</param>
    /// <param name="pendingOperatorDecisions">v0.3.8.105: the side-effecting actions this mission
    /// asked about and has no answer for, read from `approval_requests` by the caller. Passed in
    /// rather than queried here for the reason the whole type is built on — this evaluator is a
    /// PURE function of its arguments, which is what makes "graded once, and reproducibly from the
    /// record" a checkable claim. Null or empty is the state every mission before this release was
    /// in, so nothing changes for them.</param>
    /// <param name="recalledArtifacts">v0.3.8.123: a PRIOR mission's artifacts, by mission id, so a
    /// claim citing `mission:&lt;id&gt;` can be traced past the recall to whatever that mission
    /// itself rested on. A lookup rather than a store reference, and for this type's founding
    /// reason: the evaluator stays a pure function of its arguments, so a grade is still
    /// reproducible from the record — and a test can hand it a two-mission history without a
    /// database, which is the only way the cycle case is exercisable. Null traces nothing, which
    /// leaves an internal citation unresolved; every caller before this release passed nothing and
    /// no mission before it cited another mission's narrative through this gate.</param>
    public static MissionEvaluation Evaluate(Mission mission, string? stopReason, int patchProposalCount,
        MissionConstraints constraints, bool objectiveVerificationEnabled,
        IReadOnlyList<Anthill.SDK.Artifacts.Evidence>? evidence = null,
        Missions.MissionSpecification? specification = null,
        IReadOnlyList<Anthill.SDK.Artifacts.ArtifactConsumption>? consumptions = null,
        IReadOnlyList<Anthill.SDK.Artifacts.Artifact>? artifacts = null,
        IReadOnlyList<string>? pendingOperatorDecisions = null,
        Func<string, IReadOnlyList<Anthill.SDK.Artifacts.Artifact>?>? recalledArtifacts = null)
    {
        // v0.3.8.105 — A MISSION WAITING ON A PERSON HAS NOT FINISHED, WHATEVER ITS TASKS SAY.
        //
        // Resolved as a STOP REASON rather than as a fourth grading layer, because that is what it
        // is: the mission stopped, and this is why. It travels through the same field a timeout and
        // a cancellation do, is persisted with the evaluation, and is visible to every consumer
        // that already reads `StopReason` — no new channel, and nothing downstream has to learn a
        // new place to look.
        //
        // It does NOT overwrite a stop reason the runtime already produced. A mission that was
        // cancelled or timed out while a question was outstanding was stopped by the operator or by
        // the clock; the question is then moot and saying "waiting" would be false.
        var awaiting = pendingOperatorDecisions is { Count: > 0 };
        if (awaiting && string.IsNullOrWhiteSpace(stopReason))
            stopReason = MissionStopReasons.AwaitingDecision;

        // Verification layer — verdict-gated (v2.19); "not run" is distinct from "failed" for the
        // operator, but neither is a pass. v0.3.8.66: and identity-gated — the evidence store's
        // own rows must judge the final revision and tree when the mission materialized a patch.
        //
        // v0.3.8.140 — AND A REFUSAL NOW SAYS WHICH KIND IT IS. `failed` used to mean both "a
        // verdict-bearing task said no" and "nothing could establish a verdict", and those demand
        // different responses from every reader — including, below, from the structural status.
        var hasVerifier = mission.Tasks.Any(MissionVerification.IsVerificationTask);
        var verification = !hasVerifier ? MissionEvaluation.Verification.NotRun
            : MissionVerification.IsSatisfied(mission.Tasks, evidence) ? MissionEvaluation.Verification.Passed
            : MissionVerification.SomethingSaidNo(mission.Tasks) ? MissionEvaluation.Verification.Failed
            : MissionEvaluation.Verification.Inconclusive;

        // v0.3.8.140 — CLOSURE ENFORCEMENT. `docs/PLAN.md` §2e has carried this since `.118`: a
        // mission may not close COMPLETE when its own verification said no. `mission.Status` and
        // `VerificationStatus` have never met, so a mission whose verifier returned "Verification
        // Failed" was reported to the operator as `complete` with a `failed` verification beside it
        // — two lines of one record disagreeing, and the one people read first winning.
        //
        // `.122` ATTEMPTED THIS AND REVERTED IT, and the plan's instruction to the next attempt is
        // followed literally here: "Do not begin the next attempt by making the status line read
        // `VerificationStatus`." It does not. It reads the one value that means something said no,
        // which is why the demotion is now safe — an `Inconclusive` mission keeps its status, and
        // `Inconclusive` is precisely the set of cases whose demotion reclassified legitimately
        // complete missions last time.
        //
        // PARTIAL, NOT FAILED. The work happened and its tasks succeeded; what did not happen is
        // verification. Calling it `failed` would say the mission broke, which is a different and
        // wrong story about the same run — the same distinction `CloseAttempt` draws between an
        // abandoned attempt and a failed one. And only from COMPLETE: a mission already Partial or
        // Failed is not promoted by this line, which can therefore only ever reduce.
        var structural = mission.Status.Value();
        var closureRefused = mission.Status == MissionStatus.Complete
                          && string.Equals(verification, MissionEvaluation.Verification.Failed, StringComparison.Ordinal);
        if (closureRefused) structural = MissionStatus.Partial.Value();

        // Deliverable layer — "a patch proposal is a deliverable, not proof the patch is safe".
        //
        // v0.3.8.98 — AND AN ASSESSMENT'S DELIVERABLE IS ITS ANSWER. The branch below could read
        // exactly one intent, `FileChange`, so every mission that legitimately delivers an answer
        // rather than an edit resolved to `not_applicable` and was graded on the verifier alone.
        // That is the whole of what made mission 7afd85b2 gradeable as complete. The assessment
        // objective is asked FIRST, and only for the class it applies to; nothing else changes.
        var assessment = AssessmentObjective.Applies(specification)
            ? AssessmentObjective.Evaluate(specification!, evidence, consumptions, mission.FinalResult,
                // BUILT HERE, not passed in. The ledger is a pure function of the specification and
                // the terminal tasks, and the evaluator's whole claim is that a grade is
                // reproducible from the persisted record — so it derives what it can derive rather
                // than trusting a caller to have derived it the same way.
                Missions.DeliverableLedger.Build(specification, mission.Tasks))
            : null;

        // v0.3.8.99 — AND A CITATION MUST RESOLVE. The failure this catches is specific to an
        // answer built from the outside world: a claim attributed to something the mission never
        // retrieved, which reads exactly like a real citation. Applies only where there is a claim
        // record AND retrieved sources to check it against, so nothing that ran before is affected.
        // v0.3.8.109 — AND THE SECOND TRIGGER IS LIVE. Either is sufficient now: the mission's
        // contract requires retrieved sources, or it actually retrieved some. The first is what
        // makes a research mission that searched NOTHING catchable — an empty store contradicts no
        // citation, so the retrieval trigger alone reads it as "nothing to check", which is exactly
        // how a mission could be admitted to answer from the world and answer from itself instead.
        // v0.3.8.123 — AND A CITATION TO ANOTHER MISSION MUST TRACE PAST THAT MISSION. `recall_set`
        // rows make "we concluded this before" a resolvable citation; the lookup is what lets that
        // layer ask whether the recalled mission itself ever consulted anything.
        var citations = CitationIntegrity.Applies(specification, artifacts)
            ? CitationIntegrity.Evaluate(specification, artifacts, recalledArtifacts)
            : null;

        // v0.3.8.100 — AND A CREATED DELIVERABLE MUST EXIST. The failure this catches is specific
        // to a mission whose plan typed work as creation: an answer DESCRIBING a deliverable that
        // was never produced as a record, a requirement traced to a section the content does not
        // contain, an input naming a record the mission never held. Keyed on the plan's own typing,
        // so nothing that ran before is affected.
        var creations = CreationIntegrity.Applies(mission.Tasks.Select(t => t.TaskType), artifacts)
            ? CreationIntegrity.Evaluate(mission.Tasks.Select(t => t.TaskType), artifacts)
            : null;

        // v0.3.8.101 — AND A DIAGNOSIS MUST REST ON RECEIPTS. Specification-keyed like the
        // assessment gate (intake classifies this class deterministically), and mutually exclusive
        // with it by class, so the two arms below can never both speak. The failure this catches:
        // a troubleshooting mission that executed nothing, diagnosed nothing, or cited a receipt
        // no check this mission ran can account for.
        var diagnosis = DiagnosisIntegrity.Applies(specification)
            ? DiagnosisIntegrity.Evaluate(specification!, evidence, artifacts)
            : null;

        // v0.3.8.102 — AND AN OPERATION MUST HAVE HAPPENED, REVERSIBLY AND WITH PERMISSION.
        // Specification-keyed like its siblings, mutually exclusive with them by class. The
        // failure this catches: a system-action mission whose operation was described, or proposed
        // and never approved, or executed with any of its record's pieces missing.
        var operations = OperationIntegrity.Applies(specification)
            ? OperationIntegrity.Evaluate(specification!, artifacts)
            : null;

        // v0.3.8.103 — AND A SEND MUST HAVE LANDED WHERE THE HUMAN AGREED IT WOULD. Specification-
        // keyed like its siblings and mutually exclusive with them by class. What it catches that
        // no sibling can: a send that went somewhere OTHER than the approved destination — every
        // field populated, nothing missing, and still wrong.
        var sends = ExternalActionIntegrity.Applies(specification)
            ? ExternalActionIntegrity.Evaluate(specification!, artifacts)
            : null;

        // v0.3.8.106 — AND EVERY REQUEST MUST HAVE AN ANSWER. The seventh gate, and the first that
        // is not keyed to a mission CLASS: its six siblings each guard one class's own promise,
        // and this guards what any specified mission owes whatever its class — that the things the
        // operator itemised were answered.
        //
        // ASSEMBLED HERE rather than read from `mission.FinalResult`, for the reason the
        // deliverable ledger above is also built here: this evaluator's whole claim is that a grade
        // is reproducible from the persisted record. Reading the rendered prose would grade the
        // answer by looking at it, which is the word-search `.98` refused.
        // v0.3.8.109 — the evidence goes in, so each section can name the rows its own serving tasks
        // left. `.106` built the join and left it unread; §2c recorded that rendering a join is not
        // the same as making it a checkable property. `ResearchIntegrity` below is what reads it.
        var assembled = AssembledAnswer.Build(specification, mission.Tasks, mission.UserResult, evidence);
        var coverage = AnswerCoverage.Applies(assembled) ? AnswerCoverage.Evaluate(assembled) : null;

        // v0.3.8.109 — AND A RESEARCH ANSWER MUST HAVE GONE AND LOOKED. Specification-keyed like its
        // class siblings and mutually exclusive with them by class. What it catches that no sibling
        // can: a mission admitted to answer from the outside world that retrieved nothing, or whose
        // two accounts of itself — the artifacts it cites and the retrievals its evidence records —
        // do not agree, or whose plan named a step for a question and that step consulted nothing.
        var research = ResearchIntegrity.Applies(specification)
            ? ResearchIntegrity.Evaluate(specification!, artifacts, evidence, assembled, recalledArtifacts)
            : null;

        // v0.3.8.134 — AND AN ANSWER MUST HAVE ANSWERED, AND CHANGED NOTHING DOING IT. Specification-
        // keyed like its class siblings and mutually exclusive with them by class. It is the only
        // one whose finding is an absence rather than a presence: what it catches that no sibling
        // can is a mission admitted as needing nothing done that reached for a change lane anyway —
        // the recipe question answered with a proposed source patch, which no layer could refuse
        // while such requests had no class and therefore no promise to contradict.
        var answers = AnswerIntegrity.Applies(specification)
            ? AnswerIntegrity.Evaluate(specification!, mission.Tasks.Select(t => t.TaskType), artifacts, assembled)
            : null;

        // v0.3.8.104 — A RECOGNIZED CLASS IS VERIFIED WHATEVER THE SWITCH SAYS.
        //
        // `objective_verification_enabled` ships false, and every gate `.98` through `.103` built
        // sat behind it — six releases of enforcement that was inert on a default install, and the
        // reason every one of those releases could only claim "deterministically qualified": the
        // suite turned the flag on, and nobody's colony did.
        //
        // The flag is not removed, because it still governs the general and coding lanes where
        // flipping it would change how existing installs grade work they already do. What changes
        // is that a class this runtime RECOGNIZES no longer asks: its gate is the reason the class
        // exists, and a class whose gate is optional is a class whose guarantee is optional.
        //
        // AND IT FAILS CLOSED. A recognized class whose gate could not run — an unreadable store,
        // a class declaring a gate that resolves to nothing — grades NotSatisfied naming the
        // reason, never NotChecked. "We could not tell" must not read as "yes" for a mission whose
        // whole class is a promise that something specific happened.
        var recognized = specification is not null
                      && Missions.MissionContracts.RecognizedClasses.Contains(specification.MissionClass);
        var gateSpoke = assessment is not null || diagnosis is not null
                     || operations is not null || sends is not null
                     || research is not null    // v0.3.8.109
                     || answers is not null;    // v0.3.8.134 — see below; a class added to
                                                // `RecognizedClasses` without a term here fails
                                                // CLOSED, grading every mission of the new class
                                                // NotSatisfied. Adding the class and adding its
                                                // term are one change, never two.

        // v0.3.8.110 — THE UNRECOGNIZED LANE STOPS RE-READING THE COMPOSED GOAL. `mission.Goal`
        // carries the standing context and the conversation transcript below a `--- ` marker, and
        // the file-change check substring-matches verbs like "refactor" against all of it — so a
        // mission whose TRANSCRIPT contained the word acquired a deliverable requirement nobody
        // asked for and was demoted for producing no patch. `.96` paid for this lesson live in the
        // other direction: the UI gate's own refusal prose entered a transcript and re-tripped the
        // gate on every later mission, a self-sustaining refusal seeded by the gate quoting itself.
        //
        // The specification's `OriginalRequest` is the operator's own words, resolved once at
        // intake and persisted in the contract since `.104`. A null specification falls back to the
        // goal, which is exactly the pre-`.110` behaviour for every caller outside the engine.
        var operatorAsk = specification?.OriginalRequest ?? mission.Goal;

        string deliverable;
        if (recognized && !gateSpoke)
            deliverable = MissionEvaluation.Deliverable.NotSatisfied;
        else if (!objectiveVerificationEnabled && !recognized)
            deliverable = MissionEvaluation.Deliverable.NotChecked;
        // v0.3.8.106 — AN UNANSWERED REQUEST DEMOTES, and it is placed with the other two
        // cross-cutting refusals rather than in the class chain below. Citations and creations sit
        // here because they can contradict a mission of ANY class; coverage is the same shape. A
        // class gate saying "the audit inspected" cannot rescue a mission that never answered the
        // operator's second question, so this must be able to speak over a satisfied sibling.
        else if (coverage is { Satisfied: false })
            deliverable = MissionEvaluation.Deliverable.NotSatisfied;
        else if (citations is { Satisfied: false })
            deliverable = MissionEvaluation.Deliverable.NotSatisfied;
        else if (creations is { Satisfied: false })
            deliverable = MissionEvaluation.Deliverable.NotSatisfied;
        // v0.3.8.109 — the research gate joins the class chain ahead of its siblings only because
        // the chain is ordered and it has to sit somewhere; the arms are mutually exclusive by
        // class, so no order among them can change an answer. It is placed FIRST of the class arms
        // so that a reader adding the next class finds the newest one where the pattern is clearest.
        // v0.3.8.134 — placed first among the class arms for the reason `.109` gave when it took
        // the same position: the arms are mutually exclusive by class so no order among them can
        // change an answer, and the newest one sits where the next reader finds the pattern.
        else if (answers is not null)
            deliverable = answers.Satisfied
                ? MissionEvaluation.Deliverable.Satisfied
                : MissionEvaluation.Deliverable.NotSatisfied;
        else if (research is not null)
            deliverable = research.Satisfied
                ? MissionEvaluation.Deliverable.Satisfied
                : MissionEvaluation.Deliverable.NotSatisfied;
        else if (diagnosis is not null)
            deliverable = diagnosis.Satisfied
                ? MissionEvaluation.Deliverable.Satisfied
                : MissionEvaluation.Deliverable.NotSatisfied;
        else if (operations is not null)
            deliverable = operations.Satisfied
                ? MissionEvaluation.Deliverable.Satisfied
                : MissionEvaluation.Deliverable.NotSatisfied;
        else if (sends is not null)
            deliverable = sends.Satisfied
                ? MissionEvaluation.Deliverable.Satisfied
                : MissionEvaluation.Deliverable.NotSatisfied;
        else if (assessment is not null)
            deliverable = assessment.Satisfied
                ? MissionEvaluation.Deliverable.Satisfied
                : MissionEvaluation.Deliverable.NotSatisfied;
        // A SATISFIED creation gate settles the lane — but only where no assessment spoke: the
        // `.98` ledger's per-request word outranks a single record's presence, while falling all
        // the way through to the FileChange reading would grade a finished document against a
        // patch count ("write a document" contains a file-change verb, and the mission correctly
        // proposed no patch).
        else if (creations is not null)
            deliverable = MissionEvaluation.Deliverable.Satisfied;
        else if (ObjectiveVerification.Required(operatorAsk, constraints)
                 == ObjectiveVerification.Deliverable.Unknown)
            deliverable = MissionEvaluation.Deliverable.NotApplicable;
        else
            deliverable = ObjectiveVerification.IsSatisfied(mission, constraints, patchProposalCount, operatorAsk)
                ? MissionEvaluation.Deliverable.Satisfied
                : MissionEvaluation.Deliverable.NotSatisfied;

        // Generation-integrity layer (v3.0.1): if the answer was produced by a DEGRADED (non-model)
        // fallback because the routed model was unavailable, the mission cannot be a verified
        // success. A fallback/ungrounded deliverable must not score as completed_verified — this is
        // what stopped an all-fallback (provider-down) run from reporting a perfect completion.
        var generationDegraded = mission.Tasks.Any(t => t.GenerationDegraded);

        // Deterministic-block layer (v3.8.22): a reproducible check said no — the build verifier
        // failed, a patch fell outside its approved scope, or the soldier's policy engine matched a
        // blocking rule. Any one of those makes a verified outcome impossible, and none of them is a
        // judgment call that later evidence can outweigh.
        //
        // This layer exists because both signals were already computed and neither was read. The
        // verification layer above cannot cover it: it asks whether a VERIFIER TASK passed, and a
        // patch set's build failure is not a task — it is a verdict about a task's output.
        var deterministicBlock = mission.Tasks.FirstOrDefault(t => t.DeterministicBlock is not null)?.DeterministicBlock;

        // v0.3.8.101 — A REPRODUCED SYMPTOM IS NOT A BROKEN MISSION. A troubleshooting mission
        // reproduces its symptom by running a check that FAILS; the tester task carrying that
        // failure makes the structural status Partial, and Partial cannot reach a verified
        // outcome. Ungated, that grades every honest reproduction as a degraded run — teaching
        // the class to prefer missions that reproduce nothing, the exact inversion of its purpose.
        //
        // NARROW BY CONSTRUCTION, all three conditions from records: the diagnosis gate is
        // SATISFIED (receipts held, diagnosis resting on them — a failed check nothing explained
        // stays Partial); every failed task is a TESTER task (a dead researcher or builder is a
        // genuinely broken mission, whatever the checks found); and the recorded StructuralStatus
        // below keeps the honest value — only the GRADING reads the reproduction as completion,
        // and the explanation says so.
        var reproducedSymptom = diagnosis is { Satisfied: true }
            && mission.Status == MissionStatus.Partial
            && mission.Tasks.Any(t => t.Status == TaskStatus.Failed)
            && mission.Tasks.Where(t => t.Status == TaskStatus.Failed)
                .All(t => string.Equals(t.AssignedAnt, "tester", StringComparison.OrdinalIgnoreCase));

        // v0.3.8.140 — THE DEMOTION REACHES THE GRADE, not only the recorded status line.
        //
        // `structural` above is what the record SAYS; this is what the outcome is COMPUTED from,
        // and closure enforcement that moved one without the other would produce the same
        // disagreement it exists to remove — a record reading `structural=partial` beside
        // `outcome=completed_unverified`. `reproducedSymptom` already makes these two diverge in the
        // opposite direction and says so in its own remark; this makes them move together.
        //
        // The two conditions cannot collide: a reproduced symptom requires `Partial`, closure
        // refusal requires `Complete`.
        var outcome = Resolve(
            reproducedSymptom ? MissionStatus.Complete
            : closureRefused ? MissionStatus.Partial
            : mission.Status,
            stopReason, verification, deliverable, generationDegraded,
            deterministicBlock is not null);
        return new MissionEvaluation(
            MissionId: mission.Id,
            OutcomeCode: outcome,
            StructuralStatus: structural,
            VerificationStatus: verification,
            DeliverableStatus: deliverable,
            StopReason: string.IsNullOrWhiteSpace(stopReason) ? null : stopReason,
            EvaluatorVersion: Version,
            EvaluatedAt: AnthillTime.NowUtc().ToIso(),
            Explanation: Explain(outcome, structural, verification, deliverable, stopReason, generationDegraded)
                // v0.3.8.140 — a demotion an operator cannot locate is one they cannot answer, and
                // "structural=partial" on a mission whose every task succeeded names no cause.
                + (closureRefused
                    ? " Closure refused: the mission's own verification returned a verdict of no, so "
                      + "it may not close complete. The tasks ran; what did not happen is verification."
                    : "")
                + (deterministicBlock is null ? "" : $" Deterministic block: {deterministicBlock}")
                // The gate that said no, named. A demotion an operator cannot locate is one they
                // cannot answer, and "deliverable=not_satisfied" alone names no gate.
                // v0.3.8.104 — the fail-closed case names itself. A recognized class whose gate
                // could not run is refused, and an operator reading "deliverable=not_satisfied"
                // with no gate named would have nothing to act on.
                + (recognized && !gateSpoke
                    ? $" objective verification: '{specification!.MissionClass}' is a recognized "
                      + "class and its integrity gate did not run, so nothing could confirm the "
                      + "class's own guarantee. A gate that cannot run is not a pass."
                    : "")
                + (coverage is null || coverage.Satisfied ? "" : $" {coverage.Explanation}")
                + (assessment is null || assessment.Satisfied ? "" : $" {assessment.Explanation}")
                // v0.3.8.109 — the citation line is suppressed when the research gate spoke, because
                // that gate's own failure list already carries this explanation verbatim. Saying it
                // twice in one paragraph reads as two findings and is one.
                + (citations is null || citations.Satisfied || research is not null
                    ? "" : $" {citations.Explanation}")
                + (research is null || research.Satisfied ? "" : $" {research.Explanation}")
                + (answers is null || answers.Satisfied ? "" : $" {answers.Explanation}")
                + (sends is null || sends.Satisfied ? "" : $" {sends.Explanation}")
                + (creations is null || creations.Satisfied ? "" : $" {creations.Explanation}")
                + (diagnosis is null || diagnosis.Satisfied ? "" : $" {diagnosis.Explanation}")
                + (operations is null || operations.Satisfied ? "" : $" {operations.Explanation}")
                + (reproducedSymptom
                    ? " Symptom reproduced: the failed check task is the reproduction the "
                      + "diagnosis rests on, not a defect of the mission."
                    : "")
                // v0.3.8.105 — WHAT IT IS WAITING FOR, by name. An outcome that says a mission is
                // waiting and cannot say what for leaves the operator exactly where the bare
                // refusal did: told that something did not happen, with no way to make it happen.
                + (awaiting
                    ? $" Awaiting an operator decision on: {string.Join(", ", pendingOperatorDecisions!)}. "
                      + "Under the Ask policy an unanswered side-effecting action is refused — "
                      + "absence of an answer is not consent — so nothing was done and nothing "
                      + "broke. Approve or reject the pending request to settle it."
                    : ""));
    }

    private static string Resolve(MissionStatus structuralStatus, string? stopReason,
        string verification, string deliverable, bool generationDegraded, bool deterministicBlock = false)
    {
        // An interrupted mission is never completed, whatever the tasks say.
        if (stopReason == MissionStopReasons.Cancelled) return MissionOutcome.Cancelled;
        if (stopReason == MissionStopReasons.Timeout) return MissionOutcome.TimedOut;

        // v0.3.8.105 — WAITING, AND THE FIRST TIME ANYTHING HAS SAID SO.
        //
        // `MissionOutcome.WaitingForApproval` has been in the vocabulary since v2.19.0 and no
        // mission has ever carried it: every writer of a mission outcome in this tree produces one
        // of six other codes. Declared, and reaching nobody — the seventh instance of this
        // repository's house defect, and the reason a mission that stopped for an unanswered
        // question has always been graded as one that failed.
        //
        // AFTER cancel and timeout (a stopped mission is not waiting) and BEFORE the escalation
        // check and the structural grade, because both of those would answer a question that is
        // not the one being asked: the tasks did not fail, they did not run.
        if (MissionStopReasons.IsPause(stopReason)) return MissionOutcome.WaitingForApproval;

        // v0.3.8.74 — ONLY AN ESCALATING STOP ESCALATES. This line used to read
        // `if (stopReason == "adaptive_stop")`, and `adaptive_stop` was returned for two opposite
        // situations: the repair bound spent with the failure persisting, and the controller
        // declining to add a verification step the mission already had. The second is success, and
        // grading it as an escalation made a clean, fully verified patch mission unable to reach
        // `completed_verified` — which auto-apply consumes, so it could never apply its own patch.
        //
        // `MissionStopReasons.AdaptiveStopSatisfied` falls through deliberately: the mission is then
        // graded on its tasks, its verification and its deliverable, exactly as if the controller
        // had never spoken. A controller that looked, found nothing to do and said so must not
        // change the grade.
        if (MissionStopReasons.IsEscalation(stopReason)) return MissionOutcome.Escalated;

        if (structuralStatus == MissionStatus.Partial) return MissionOutcome.Partial;
        if (structuralStatus is not MissionStatus.Complete) return MissionOutcome.FailedPermanent;

        // Structural completion + verifier PASS + deliverable produced (where the layer is active
        // and applicable). NotSatisfied is the only deliverable state that demotes — a disabled
        // layer keeps pre-v2.26 behaviour, and is visible as "not_checked" rather than hidden.
        var verified = verification == MissionEvaluation.Verification.Passed
                       && deliverable != MissionEvaluation.Deliverable.NotSatisfied
                       && !generationDegraded
                       && !deterministicBlock;   // v3.8.22 — a reproducible "no" is final
        return verified ? MissionOutcome.CompletedVerified : MissionOutcome.CompletedUnverified;
    }

    private static string Explain(string outcome, string structural, string verification,
        string deliverable, string? stopReason, bool generationDegraded) =>
        $"outcome={outcome} (structural={structural}, verification={verification}, "
        + $"deliverable={deliverable}{(generationDegraded ? ", generation=degraded" : "")}"
        + $"{(stopReason is null ? "" : $", stop={stopReason}")})";
}
