namespace Anthill.SDK.Contracts;

// v3.8.9 — the HALF of the old Anthill.Core.Contracts.TaskContracts that is genuinely shared
// vocabulary: what a capability is and how a failure is classified. Nothing here knows what a
// mission or a task is.
//
// v0.3.8.87 removed the third thing it used to hold — a per-role declaration of capabilities and
// side effects that duplicated the contracts and disagreed with them. See the note at the bottom of
// this file; the `System.Text.Json.Serialization` import went with it, because nothing left here is
// serialized.
//
// The other half stayed in the core, and the reason is the whole lesson of this split. An earlier
// attempt moved the entire file after checking its `using` statements and finding none — but
// `TaskContract.FromTask` takes `Domain.Task` and reaches `Agents.AntRegistry`, and `ContractGate`
// takes `List<Domain.Task>`, all through PARTIAL qualification that resolved via the enclosing
// namespace and left no import to notice. Those three types are core planning logic that happened
// to share a file with five pure ones.
//
// `ToolResult` also stayed, deliberately: Anthill.Core.Domain declares a DIFFERENT ToolResult, and
// call sites disambiguate with `Contracts.ToolResult`. Moving it would have broken every one of
// them in a way that reads as an unrelated ambiguity error.

/// <summary>
/// v2.9.0 — Contracted Tasks and Typed Capability Tools (NORTH_STAR V3-track Phase 2).
/// Machine-readable contracts replace loose prompt tasks and string-parsed tool results as the
/// control-flow surface: planner output is schema-validated (invalid tasks cannot enter the
/// execution queue), permissions attach to CAPABILITIES rather than ant names and are evaluable
/// before execution, and failures are classified by a fixed taxonomy that drives retry decisions.
/// </summary>
public static class Capability
{
    public const string RepoRead = "repo.read";
    public const string RepoSearch = "repo.search";
    public const string RepoWriteSandbox = "repo.write.sandbox";
    public const string RepoPatchPropose = "repo.patch.propose";
    public const string RepoPatchApply = "repo.patch.apply";
    public const string ProcessExecuteReadonly = "process.execute.readonly";
    public const string NetworkHttpPublic = "network.http.public";
    public const string NetworkHttpHomelab = "network.http.homelab";
    public const string ModelInvoke = "model.invoke";
    public const string ProxmoxRead = "proxmox.read";
    public const string ProxmoxVmStart = "proxmox.vm.start";
    public const string ProxmoxVmStop = "proxmox.vm.stop";
    public const string ProxmoxSnapshotCreate = "proxmox.snapshot.create";
    public const string CredentialUse = "credential.use";
}

/// <summary>The fixed failure taxonomy. Retry decisions come from the class, never from parsing
/// error strings.</summary>
public enum FailureClass
{
    None = 0,
    ValidationFailure, AuthorizationFailure, TargetRejection,
    TransientProviderFailure, RateLimit, Timeout, Conflict,
    DependencyFailure, VerificationFailure, UnsafeState,
    CompensationFailure, InternalDefect,

    // Structural-repair release — the taxonomy the failure boundary actually needs. APPENDED,
    // never reordered: the wire form is the snake_case NAME (FailureClassNames), but keeping
    // numeric order stable costs nothing and protects any consumer that cached values.
    /// <summary>The provider is reachable and answered "no" in a way retries will not change —
    /// invalid key, model decommissioned, permanent 4xx.</summary>
    PermanentProviderFailure,
    /// <summary>No effective model could be resolved for the role — missing model, empty route,
    /// or a route naming a model the provider does not serve.</summary>
    ModelRoutingFailure,
    /// <summary>A tool dispatch failed for reasons other than authorization — the tool errored,
    /// not the gate.</summary>
    ToolFailure,
    /// <summary>A deterministic POLICY said no. Distinct from AuthorizationFailure (a capability
    /// gate) because recovery must never route around policy.</summary>
    PolicyDenial,
    /// <summary>A typed artifact failed its schema or integrity check — the input was structurally
    /// unusable, whoever produced it.</summary>
    InvalidArtifact,
    /// <summary>A patch could not apply: stale base hash, missing target, conflicting content.</summary>
    PatchConflict,
    /// <summary>The build check failed. A subtype of deterministic-check failure that names the
    /// toolchain, because "build broke" and "tests broke" route differently.</summary>
    BuildFailure,
    /// <summary>The test check failed.</summary>
    TestFailure,
    /// <summary>A security review or scan produced a blocking finding.</summary>
    SecurityFailure,
    /// <summary>The work was cancelled — operator stop or linked-token cancellation. Not an error
    /// and never a defect.</summary>
    Cancellation,
    /// <summary>The failure could not be classified at the boundary. UNKNOWN STAYS UNKNOWN — it is
    /// never collapsed into InternalDefect, never auto-non-retryable-with-a-story. A consumer must
    /// treat it as "insufficient evidence" and gather more or escalate, not diagnose it.</summary>
    UnknownFailure,
}

public static class FailureClassify
{
    /// <summary>Only these classes may be retried automatically; everything else needs a human
    /// or a plan change. Unknown is NOT auto-retryable — but see <see cref="IsKnown"/>: unknown
    /// is also not evidence of a permanent defect, and a consumer deciding recovery must
    /// distinguish "retry will not help" from "we do not know what happened".</summary>
    public static bool IsRetryable(FailureClass c) => c is FailureClass.TransientProviderFailure
        or FailureClass.RateLimit or FailureClass.Timeout or FailureClass.Conflict;

    /// <summary>False for None and UnknownFailure — the two states that mean "unclassified",
    /// which no recovery decision may treat as a diagnosis.</summary>
    public static bool IsKnown(FailureClass c) => c is not (FailureClass.None or FailureClass.UnknownFailure);

    /// <summary>Classes where recovery must stop and escalate rather than repair or retry —
    /// routing around a policy or security "no" is never a repair.</summary>
    public static bool MustEscalate(FailureClass c) => c is FailureClass.PolicyDenial
        or FailureClass.SecurityFailure or FailureClass.AuthorizationFailure;
}

/// <summary>
/// The ONE conversion between <see cref="FailureClass"/> and its string form. v3.8.32.
///
/// Before this existed the codebase had two string representations of the same enum and no agreement
/// about which was which:
///
/// <list type="bullet">
/// <item><c>TaskOutcomeMapper</c> wrote <c>transient_provider_failure</c> into <c>Task.FailureType</c>,
///   which flowed on into <c>task_attempts.failure_class</c>.</item>
/// <item><c>SqliteMemory.RecordTaskResult</c> wrote <c>TransientProviderFailure</c> into
///   <c>task_results.failure_class</c>.</item>
/// <item><c>LearningAttribution</c> compared the FIRST against the SECOND's form, with
///   <c>OrdinalIgnoreCase</c> — which bridges the casing and NOT the underscores.</item>
/// </list>
///
/// The consequence ran for six releases: the environmental-failure set matched nothing, so every
/// provider outage, rate limit, timeout, dependency failure and authorization refusal was charged as
/// a negative pheromone trail against whichever ant was holding the task. That is the precise bug
/// v3.8.26 was written to fix, and it never once worked. <c>LoadTaskResult</c>'s
/// <c>Enum.TryParse</c> had the mirror-image blind spot, and <c>WhatUsuallyFails</c> grouped the two
/// forms into separate buckets, reporting one failure class as two.
///
/// Every one of those sites had a passing test, because each test built its own input in the form
/// its own side expected. No test anywhere ran a value from a real producer into a real consumer.
///
/// So the fix is not "pick a format" — it is to remove the choice. There is one <see cref="Wire"/>
/// out and one <see cref="TryParse"/> in, <see cref="TryParse"/> normalises away the difference the
/// old code tripped on, and no caller anywhere is permitted to call <c>.ToString()</c> on a
/// <see cref="FailureClass"/> destined for storage or comparison.
/// </summary>
public static class FailureClassNames
{
    /// <summary>
    /// The canonical wire form: <c>snake_case</c>.
    ///
    /// Chosen because it is what the rest of the wire vocabulary already uses — status codes
    /// (<c>failed_retryable</c>), trail kinds (<c>model_route</c>), and the untyped failure types the
    /// runtime writes directly (<c>execution_error</c>, <c>missing_ant</c>). The PascalCase form was
    /// never a decision; it was <c>.ToString()</c> reached for at three separate call sites.
    /// </summary>
    private static readonly Dictionary<FailureClass, string> WireByClass =
        Enum.GetValues<FailureClass>().ToDictionary(c => c, c => ToSnake(c.ToString()));

    /// <summary>
    /// Lookup keyed by a NORMALISED form — lowercased with underscores removed — so both historical
    /// representations resolve to the same class.
    ///
    /// This is deliberate rather than lenient. Databases in the field already hold both forms, written
    /// by the two producers above; a parser that accepted only the new canonical form would silently
    /// drop every row written before this release, which is the same failure mode in a new coat.
    /// </summary>
    private static readonly Dictionary<string, FailureClass> ByNormalized =
        Enum.GetValues<FailureClass>().ToDictionary(c => Normalize(c.ToString()), c => c);

    /// <summary>Every canonical wire name. Ordered by the enum, so it is stable across runs.</summary>
    public static IReadOnlyCollection<string> AllWire { get; } =
        Enum.GetValues<FailureClass>().Select(c => WireByClass[c]).ToArray();

    /// <summary>The canonical string for a class. The ONLY permitted way to stringify one.</summary>
    public static string Wire(FailureClass cls) =>
        WireByClass.TryGetValue(cls, out var name) ? name : ToSnake(cls.ToString());

    /// <summary>
    /// Parse any recorded form back to the class. Accepts the canonical wire form and the legacy
    /// enum-name form; rejects anything else rather than guessing.
    /// </summary>
    /// <remarks>
    /// Returns false for the runtime's untyped failure types (<c>timeout</c> is a member, but
    /// <c>missing_ant</c>, <c>execution_error</c> and <c>blocked</c> are not). A caller must treat
    /// false as "this failure was not classified", never as "this failure was benign".
    /// </remarks>
    public static bool TryParse(string? text, out FailureClass cls)
    {
        cls = FailureClass.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return ByNormalized.TryGetValue(Normalize(text), out cls);
    }

    /// <summary>Parse, or <see cref="FailureClass.None"/> when the text names no known class.</summary>
    public static FailureClass ParseOrNone(string? text) => TryParse(text, out var cls) ? cls : FailureClass.None;

    /// <summary>
    /// Casing- and separator-insensitive key. Underscores are REMOVED rather than treated as
    /// significant, which is exactly the step the old <c>OrdinalIgnoreCase</c> comparison was
    /// missing.
    /// </summary>
    private static string Normalize(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch != '_' && ch != '-' && ch != ' ') sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static string ToSnake(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 6);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }
}

/// <summary>
/// THERE IS NO SECOND CATALOG. v0.3.8.87 — and what used to be here is worth recording, because this
/// file is where the duplicate would come back.
///
/// A `ToolDescriptor` type and a `ToolCatalog` holding six of them lived here, giving each role a
/// `RequiredCapabilities` list, a `SideEffectClass`, a `RiskClass` and a `Compensation`.
/// `Anthill.Core.Agents.AntExecutionCatalog` declares the same facts for all twelve roles. Two
/// implementations of one rule — and only the second was ever enforced.
///
/// `ToolAuthorization.Evaluate` reads the CONTRACT and refuses a dispatch the grant does not cover.
/// The catalog here was read by `TaskContract.FromTask`, which feeds `ContractGate.Admit`, which
/// decides whether a planned task may enter the execution queue. So the ADMISSION gate and the
/// DISPATCH gate answered the same question from different books, and the books disagreed:
///
/// <list type="bullet">
/// <item>`researcher` — the contract requires repo.read and repo.search; the catalog claimed
///   model.invoke alone.</item>
/// <item>`coder` and `verifier` — the catalog added repo.read that neither contract requires.</item>
/// <item>`builder` — the catalog required <c>repo.write.sandbox</c>, which `CapabilityGrant` is
///   written never to grant, in a comment that names it. A requirement nothing could satisfy,
///   declared beside a check nothing ran.</item>
/// <item>Every contract declares <c>AllowsSideEffects: false</c>; the catalog called the coder and
///   the builder "reversible" with manual compensation.</item>
/// <item>The archivist, medic, tester, soldier, scribe and ui_cartographer had no entry at all, so
///   the projection's fallback declared <c>model.invoke</c> for all six — the exact lie v0.3.8.76
///   deleted from the archivist's contract, preserved here because nobody read both books at once.</item>
/// </list>
///
/// `ToolCatalog.CanRun` — the pre-execution permission check that lived here — had no production
/// caller in its entire life. Its one caller was a test that built the descriptor AND the grant set
/// itself and asserted they matched, which is the failure <see cref="FailureClassNames"/> records a
/// few lines above, in those words: *no test anywhere ran a value from a real producer into a real
/// consumer.*
///
/// So the fix is not to reconcile the two lists. It is to remove the choice, the same way
/// <see cref="FailureClassNames"/> removed it for the wire format: the contracts declare what a role
/// requires and what it may do, `TaskContract.FromTask` derives the side-effect projection from
/// them, and `CapabilityDeclarationTests.OnlyTheRoleContracts_DeclareRoleCapabilities` fails if a
/// second declaration appears in this file again.
///
/// <see cref="Capability"/> stays. It is the vocabulary both halves named, and the one thing here
/// that was never duplicated.
/// </summary>
public static class ToolVocabularyHistory
{
    /// <summary>The release that removed the second catalog. Referenced by the guard's message so a
    /// reader who trips it lands on the paragraph above rather than on a diff.</summary>
    public const string SecondCatalogRemovedIn = "v0.3.8.87";
}
