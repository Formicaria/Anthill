using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>
/// WHO ASKED FOR THIS PHYSICAL WORK. v0.3.8.114.
///
/// Every physical request carries one, and every one travels the same pipeline. The brief is
/// explicit that there must not be a `ManualMicromoundController` and an
/// `AutonomousMicromoundController`, and this enum is what makes a second path unnecessary rather
/// than merely discouraged: origin is an INPUT to policy, not a choice of code path.
///
/// The names track Anthill's own provenance vocabulary where it has one — `PromotionActor` already
/// distinguishes a human from `Automation` for patch promotion, and this is the same distinction
/// asked about physical work. It is declared here rather than reused directly because the module
/// may not reference the core; the alignment is deliberate and worth keeping if either changes.
/// </summary>
public enum PhysicalOrigin
{
    /// <summary>A person clicked something. The only origin a newly enrolled mound will act on.</summary>
    User = 0,

    /// <summary>The Queen decided physical work was needed while planning a mission.</summary>
    Queen = 1,

    /// <summary>A defined workflow reached a step that needs the physical world.</summary>
    Workflow = 2,

    /// <summary>The autonomy Director, running unattended.</summary>
    Automation = 3,

    /// <summary>The colony itself — health sweeps, scheduled routines, reconciliation.</summary>
    System = 4,
}

/// <summary>
/// The wire spelling of an origin, and the one place a string becomes one. v0.3.8.114.
///
/// `Enum.TryParse` is deliberately NOT used: it understands the MEMBER NAME and nothing else, so it
/// would accept "Queen" and refuse "queen" — and `CrossBoundaryAgreementTests` already refuses that
/// pattern across this repository for exactly the reason it bites here. An origin that fails to
/// parse must be a refusal rather than a default, because the default that suggests itself is
/// <see cref="PhysicalOrigin.User"/>, which is the MOST permissive one: an unreadable origin
/// silently becoming "a person asked" is autonomy granted by a typo.
/// </summary>
public static class PhysicalOrigins
{
    public static readonly IReadOnlyList<string> All =
        [.. Enum.GetValues<PhysicalOrigin>().Select(Wire)];

    public static string Wire(PhysicalOrigin origin) => origin switch
    {
        PhysicalOrigin.User => "user",
        PhysicalOrigin.Queen => "queen",
        PhysicalOrigin.Workflow => "workflow",
        PhysicalOrigin.Automation => "automation",
        PhysicalOrigin.System => "system",
        _ => "user",
    };

    /// <summary>
    /// Parse a wire origin. An ABSENT one resolves to <see cref="PhysicalOrigin.User"/> — the
    /// caller is a person holding a session token — but a PRESENT and unreadable one is refused.
    /// </summary>
    public static bool TryParse(string? value, out PhysicalOrigin origin)
    {
        origin = PhysicalOrigin.User;
        if (string.IsNullOrWhiteSpace(value)) return true;

        foreach (var candidate in Enum.GetValues<PhysicalOrigin>())
            if (string.Equals(Wire(candidate), value.Trim(), StringComparison.Ordinal))
            {
                origin = candidate;
                return true;
            }

        return false;
    }
}

/// <summary>
/// What a mound will accept, and from whom. Conservative by default, and deliberately small: three
/// states an operator can hold in their head, not a matrix.
/// </summary>
public enum AutonomyPolicy
{
    /// <summary>
    /// A person asks, every time. THE DEFAULT FOR A NEWLY ENROLLED MOUND — a device that just
    /// appeared has proved nothing about itself, and the presence of a controller that technically
    /// supports autonomous dispatch is not a reason to grant it.
    /// </summary>
    ManualOnly = 0,

    /// <summary>
    /// Anything may ASK, and a person answers before it happens. The middle state, and the one a
    /// fleet spends most of its life in: the Queen can propose physical work and an operator
    /// approves it through the colony's ordinary approval pipeline.
    /// </summary>
    ApprovalRequired = 1,

    /// <summary>
    /// Anything may act WITHIN THE CHARTER ALREADY ISSUED — and not one capability beyond it. The
    /// charter is the grant; this only decides who may spend it. An operator raising a mound to
    /// this level is saying "the bounds I already wrote are the bounds I meant", which is why it is
    /// safe to state so briefly: the interesting decision was made when the charter was signed.
    /// </summary>
    WithinCharter = 2,
}

/// <summary>The answer, with the reason attached. A refusal without a reason is a contract violation.</summary>
/// <param name="Allowed">May the colony issue this at all?</param>
/// <param name="RequiresApproval">Must a person answer first? Meaningless when not allowed.</param>
/// <param name="Reason">Always populated, including on the allow — the audit trail wants both.</param>
public sealed record PolicyVerdict(bool Allowed, bool RequiresApproval, string Reason)
{
    public static PolicyVerdict Refused(string reason) => new(false, false, reason);
}

/// <summary>
/// THE POLICY SEAM — §17, built now and left conservative. v0.3.8.114.
///
/// This release does not give the Queen autonomous physical control, and it is not supposed to.
/// What it must not do is make that a LATER ARCHITECTURAL CHANGE: the brief's test is that a future
/// release can satisfy the autonomy flow "without changing Micromound transport or adding another
/// physical control API". So the seam exists, every origin passes through it, and the only thing a
/// future release changes is a policy value on a mound record.
///
/// WHY POLICY IS NOT AUTHORITY, and the distinction is the whole design. A charter says WHAT may
/// happen — which capabilities, within which limits, until when — and the mound enforces it. This
/// says WHO MAY SPEND IT, and only Anthill enforces it. They compose in one direction: policy can
/// only ever narrow what a charter already granted, never widen it, and a mound that receives a
/// mission still checks it against the charter regardless of what any policy here concluded.
///
/// So the worst case if this type is wrong is a mission the mound refuses. That asymmetry is
/// deliberate — it is why the policy seam can be simple, and why it must never be the only gate.
///
/// AN APPROVAL IS DECIDED HERE AND CREATED ELSEWHERE. §19 says to reuse Anthill's approval system
/// rather than build a second one, and this module may not reference the core where that system
/// lives. So the verdict says an approval is REQUIRED and the composition root raises a real
/// Anthill approval request. The module never grows its own.
/// </summary>
public static class MicromoundAutonomy
{
    /// <summary>
    /// Evaluate one physical request. Pure: no store, no clock, no I/O — the same inputs give the
    /// same answer, which is what lets the audit record the decision rather than a description of it.
    /// </summary>
    /// <param name="policy">The mound's policy.</param>
    /// <param name="origin">Who asked.</param>
    /// <param name="ceiling">The action class this request needs, from the charter it runs under.</param>
    /// <param name="stopped">Whether a stop is in force. Outranks everything below.</param>
    public static PolicyVerdict Evaluate(
        AutonomyPolicy policy, PhysicalOrigin origin, ActionClass ceiling, bool stopped)
    {
        // STOP WINS, ALWAYS, AND FIRST. SAFETY.md gives stop precedence over "missions,
        // configuration, routine work, autonomy, backlog" — autonomy is named in that list, so a
        // policy that could reach past a stop would be the exact thing that sentence forbids. It is
        // checked before the policy is even read so there is no ordering to get wrong later.
        if (stopped) return PolicyVerdict.Refused("a stop is in force");

        // Hazardous is never reached through a standing policy. SAFETY.md Layer 2 authorizes it per
        // action, expiring on use, and that pipeline does not exist yet — "until that pipeline ships
        // with tests, hazardous actions are refused unconditionally."
        if (ceiling == ActionClass.Hazardous)
            return PolicyVerdict.Refused(
                "hazardous work is authorized per action and that pipeline has not shipped");

        // A PERSON MAY ALWAYS ASK, whatever the policy. ManualOnly is a bound on AUTONOMY, not a
        // bound on the operator — reading it as "nothing may happen" would leave a mound an
        // operator could not drive at all, which is not what any of the three states mean.
        if (origin == PhysicalOrigin.User)
            return new PolicyVerdict(true, RequiresApproval: false, "requested by an operator");

        return policy switch
        {
            AutonomyPolicy.ManualOnly => PolicyVerdict.Refused(
                $"this mound is manual-only; a {Describe(origin)} request needs an operator to raise its policy"),

            AutonomyPolicy.ApprovalRequired => new PolicyVerdict(
                true, RequiresApproval: true,
                $"a {Describe(origin)} request on an approval-required mound"),

            // Within the charter, and the charter is what bounds it. Note what is NOT re-checked
            // here: which capabilities, which limits, how long. Those are the charter's, they were
            // decided when it was signed, and the mound enforces them. Re-deciding them here would
            // be a second implementation of one rule.
            AutonomyPolicy.WithinCharter => new PolicyVerdict(
                true, RequiresApproval: false,
                $"a {Describe(origin)} request within the mound's existing charter"),

            // An unknown policy value resolves DOWNWARD. A colony reading a record written by a
            // newer version must refuse rather than guess, because the guess that costs nothing to
            // make is the one that acts.
            _ => PolicyVerdict.Refused($"unknown autonomy policy '{policy}'"),
        };
    }

    /// <summary>Parse a stored policy name, resolving anything unrecognised downward.</summary>
    public static AutonomyPolicy Parse(string? value) => value switch
    {
        "approval_required" => AutonomyPolicy.ApprovalRequired,
        "within_charter" => AutonomyPolicy.WithinCharter,
        _ => AutonomyPolicy.ManualOnly,
    };

    /// <summary>The stored spelling. Snake case, like everything else the colony persists.</summary>
    public static string Value(AutonomyPolicy policy) => policy switch
    {
        AutonomyPolicy.ApprovalRequired => "approval_required",
        AutonomyPolicy.WithinCharter => "within_charter",
        _ => "manual_only",
    };

    private static string Describe(PhysicalOrigin origin) => origin switch
    {
        PhysicalOrigin.Queen => "Queen-originated",
        PhysicalOrigin.Workflow => "workflow-originated",
        PhysicalOrigin.Automation => "automation-originated",
        PhysicalOrigin.System => "system-originated",
        _ => "user-originated",
    };
}
