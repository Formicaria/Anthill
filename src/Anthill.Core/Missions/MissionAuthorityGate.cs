using Anthill.SDK.Contracts;

namespace Anthill.Core.Missions;

/// <summary>
/// THE CEILING, FINALLY READ. v0.3.8.103.
///
/// WHAT WAS WRONG. <see cref="MissionAuthority"/> has existed since `.98` with a doc comment
/// calling it "the ceiling on what the mission may DO, agreed across specification, operator
/// policy, worker contract and adapter before dispatch". Intake set it. The mission snapshot showed
/// it. Tests asserted it. And no dispatch anywhere ever consulted it — five releases of a value
/// that described a guarantee nobody enforced. That is this repository's named house defect, the
/// same one that made `.98`'s capability branch compile, read correctly and never execute once, and
/// the same one that left `manage_models` required by an endpoint and absent from the permission
/// table. A declaration reaching nobody is worse than an absent one, because everything downstream
/// believes it.
///
/// WHAT THIS IS. One table, from a side-effecting action to the authority a mission must hold to
/// reach it, and one function that compares it to the mission's ceiling. Deliberately small: the
/// gate's value is that it is UNIVERSAL — it does not know about mission classes, and adding a
/// class cannot forget to be covered by it.
///
/// WHY A CEILING IS NOT A SECOND ESCALATION GATE, and the distinction is the whole design. The
/// escalation lane asks "did a human decide?", per action, at dispatch. This asks "is this mission
/// the KIND of mission that may do this at all?", once, from what intake resolved. They refuse
/// different things and neither substitutes for the other: an audit mission that somehow reached
/// an execute tool is not fixed by an operator clicking approve, because the operator approved an
/// audit. Both must pass. Neither is weakened to let the other decide.
///
/// A TOOL WITH NO ENTRY IS UNAFFECTED, and that is deliberate rather than a gap. Reading is not
/// governed by a ceiling — a ceiling that refused reading would make every audit a Modify mission,
/// which is the opposite of what an audit is. What must never happen is a SIDE-EFFECTING action
/// with no entry, and that is asserted rather than trusted: `ExternalActionMissionTests` sweeps
/// <c>EscalationGate.SideEffecting</c> and fails on any member this table does not name.
/// </summary>
public static class MissionAuthorityGate
{
    /// <summary>The verdict, in the shape every other gate in this repository uses.</summary>
    public sealed record Decision(bool Allowed, string Reason)
    {
        public static readonly Decision Ok = new(true, "");
    }

    /// <summary>
    /// What each side-effecting action requires. The values are the authority the ACTION needs, not
    /// the authority any particular class happens to have — the two must be able to disagree, or
    /// the table would just be restating intake.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, MissionAuthority> Requirements =
        new Dictionary<string, MissionAuthority>(StringComparer.OrdinalIgnoreCase)
        {
            // Changes the operator's own tree. Modify since the coding lane existed; named here for
            // the first time.
            ["apply_patch"] = MissionAuthority.Modify,
            ["write_text_file"] = MissionAuthority.Modify,
            ["shell_command"] = MissionAuthority.Modify,

            // Runs a declared, allowlisted check. This is the level `.101`'s diagnostic class was
            // given precisely so that executing a check would not require the authority to change
            // things — the distinction is load-bearing and this is where it becomes enforceable.
            ["run_allowlisted_check"] = MissionAuthority.ExecuteChecks,

            // `.102`: proposing writes a colony-database row and changes nothing outside the
            // process, so it needs no ceiling; executing reaches the operator's infrastructure.
            [SystemActionToolNames.Execute] = MissionAuthority.Modify,

            // `.103`: irreversible the instant it lands, and read by people the colony cannot
            // reach. Resolution and proposal are local and ungated here for the same reason
            // `propose_system_action` is.
            [ExternalActionToolNames.Execute] = MissionAuthority.Modify,

            // Starting a mission is the conversation's own escalation, not a mission's — a mission
            // does not start missions. Named at Observe so the sweep cannot pass by omission: the
            // entry says "considered, needs nothing", which is a different fact from "absent".
            [Anthill.Core.Conversations.ConversationRunner.StartMissionAction] = MissionAuthority.Observe,
        };

    /// <summary>
    /// The authority this action needs, or null when it is not ceiling-governed. Null is the
    /// answer for every read-only tool, which is most of them.
    /// </summary>
    public static MissionAuthority? Required(string? action) =>
        !string.IsNullOrWhiteSpace(action) && Requirements.TryGetValue(action!, out var needed)
            ? needed
            : null;

    /// <summary>
    /// May a mission holding <paramref name="ceiling"/> reach <paramref name="action"/>?
    ///
    /// The refusal names BOTH levels, because "not authorized" tells an operator nothing they can
    /// act on: the fix is either to ask for a different kind of mission or to accept that this one
    /// cannot do that, and choosing between them requires knowing which ceiling was in force and
    /// which was needed. "Failure messages must name the layer that said no" — this one names the
    /// layer and both of its numbers.
    /// </summary>
    public static Decision Evaluate(MissionAuthority ceiling, string? action)
    {
        var required = Required(action);
        if (required is null || ceiling >= required.Value) return Decision.Ok;

        return new Decision(false,
            $"mission authority: '{action}' requires {required.Value} authority and this mission was "
          + $"admitted at {ceiling}. The ceiling is set once at intake from what the operator asked "
          + "for; an operator decision cannot raise it, because the decision approved a mission of "
          + "this kind.");
    }
}
