using Anthill.Core.Configuration;

namespace Anthill.Core.Agents;

/// <summary>
/// Execution framework Stage C — the validated runtime catalog. Queen's executor dictionary is
/// validated at startup against the registry, the Stage A contracts, and the feature gates; the
/// result is a per-role availability snapshot the planner, API, and UI can trust. Fail closed:
/// a role that is gated off, missing a handler, or missing a contract is runtime-unavailable with
/// an explicit reason — it can never be silently assigned work.
/// </summary>
public sealed record RoleAvailability(
    string RoleId,
    AntRuntimeKind RuntimeKind,
    bool Implemented,
    bool Enabled,
    bool PlannerEligible,
    bool RuntimeAvailable,
    string UnavailabilityReason);

/// <summary>
/// Whether a role's rollout gate permits it to run, and when it does not, WHICH gate closed it.
/// v3.8.32.
///
/// <see cref="NotGated"/> is the member that matters. The six core ants have no rollout flag, no
/// tier requirement and no master switch — they are the colony. Every predicate that answered this
/// question before returned <c>bool</c>, so "not subject to gating" had nowhere to go and came back
/// as <c>false</c>, indistinguishable from "gated shut".
/// </summary>
public enum RoleGateStatus
{
    /// <summary>A core ant. No gate applies; readiness must not consult one.</summary>
    NotGated,
    /// <summary>A gated specialist whose gates are all open.</summary>
    Open,
    /// <summary>Specialist execution is disabled colony-wide.</summary>
    ClosedByMasterSwitch,
    /// <summary>The activation tier's ceiling does not admit this role.</summary>
    ClosedByTier,
    /// <summary>The tier admits it, but the role's own rollout flag is off.</summary>
    ClosedByRolloutFlag,
}

public static class AntExecutorCatalog
{
    private static readonly object Lock = new();
    private static IReadOnlyDictionary<string, RoleAvailability> _snapshot =
        new Dictionary<string, RoleAvailability>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, RoleAvailability> Snapshot { get { lock (Lock) return _snapshot; } }

    /// <summary>
    /// The six roles that are BEHIND a rollout gate. Declared as data, once. v3.8.32.
    ///
    /// This set used to exist only as the arms of the switch in <see cref="SpecialistGateOpen"/>,
    /// where its default arm returned <c>false</c>. That is correct for the question the method
    /// asks — "is this specialist's rollout flag open?" — and catastrophic for a caller that asks it
    /// of a core ant, because "researcher has no rollout flag" and "researcher's rollout flag is
    /// off" came back as the same value.
    ///
    /// The <c>/colony/registry</c> readiness surface did exactly that, over all twelve contract
    /// keys, and reported the six core ants as blocked by flags that do not exist.
    /// </summary>
    public static readonly IReadOnlySet<string> GatedRoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "tester", "soldier", "medic", "archivist", "ui_cartographer", "scribe" };

    /// <summary>Whether a role is subject to rollout gating at all. Core ants are not.</summary>
    public static bool IsGated(string roleId) => GatedRoles.Contains(roleId ?? "");

    /// <summary>
    /// Why a role is or is not permitted to run, as a value that CANNOT be confused with a boolean.
    ///
    /// The defect this replaces was not a wrong condition — every individual condition was right. It
    /// was a two-valued answer to a three-valued question. <see cref="RoleGateStatus.NotGated"/> is
    /// the state that had no representation, so it collapsed into "closed".
    /// </summary>
    public static RoleGateStatus GateStatusOf(string roleId)
    {
        if (!IsGated(roleId)) return RoleGateStatus.NotGated;
        if (!AnthillRuntime.EnableSpecialistAntExecution) return RoleGateStatus.ClosedByMasterSwitch;
        if (!ActivationTiers.Admits(AnthillRuntime.ActivationTier, roleId)) return RoleGateStatus.ClosedByTier;
        return RolloutFlagOpen(roleId) ? RoleGateStatus.Open : RoleGateStatus.ClosedByRolloutFlag;
    }

    /// <summary>The role's own rollout flag. Meaningful ONLY for a gated role.</summary>
    private static bool RolloutFlagOpen(string roleId) => roleId switch
    {
        "tester" => AnthillRuntime.EnableTesterAnt,
        "soldier" => AnthillRuntime.EnableSoldierAnt,
        "medic" => AnthillRuntime.EnableMedicAnt,
        "archivist" => AnthillRuntime.EnableArchivistAnt,
        "ui_cartographer" => AnthillRuntime.EnableUiCartographerAnt,
        "scribe" => AnthillRuntime.EnableScribeAnt,
        _ => false,
    };

    /// <summary>
    /// Specialist roles arriving via the framework (Stage D canaries) and their gates.
    ///
    /// v2.22.0: THREE conditions, all required — the master switch, the activation tier's ceiling,
    /// and the role's own rollout flag. The tier can only ever narrow: a role whose flag is off
    /// stays off at `full`, so raising the tier never switches anything on by itself and every
    /// existing rollout gate remains exactly as binding as before.
    ///
    /// v3.8.32: delegates to <see cref="GateStatusOf"/>. Behaviour for its three existing callers is
    /// unchanged — all of them ask only about gated roles — but the name still promises "specialist",
    /// so a caller holding an arbitrary role id should use <see cref="GateStatusOf"/> instead and let
    /// the compiler make it handle <see cref="RoleGateStatus.NotGated"/>.
    /// </summary>
    public static bool SpecialistGateOpen(string roleId) => GateStatusOf(roleId) == RoleGateStatus.Open;

    /// <summary>Build + validate the catalog. <paramref name="handlerRoleIds"/> is the set of
    /// role ids that have a real runtime handler instance. Returns startup problems (empty = clean).</summary>
    public static List<string> Initialize(IReadOnlyCollection<string> handlerRoleIds)
    {
        var problems = new List<string>();
        var snap = new Dictionary<string, RoleAvailability>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in AntRegistry.Roles)
        {
            var id = role.RoleId;
            var kind = AntExecutionCatalog.KindOf(id);
            var hasHandler = handlerRoleIds.Contains(id);
            var hasContract = AntExecutionCatalog.ContractFor(id) is not null;
            // v3.8.23: renamed from `isSpecialist`. It has always meant "has a contract", and that
            // was the same thing as "is a specialist" only for as long as the six core ants had
            // none. Now every mission role does, so the old name would read as a claim about
            // rollout tier while testing something else entirely — which is how the missing-contract
            // check below came to be unreachable for exactly the roles that needed it.
            var hasDeclaredContract = AntExecutionCatalog.Contracts.ContainsKey(id);

            bool implemented, available;
            string reason = "";

            if (kind is AntRuntimeKind.ControlPlane or AntRuntimeKind.DeterministicService)
            {
                implemented = true; available = false; // never mission-schedulable
                reason = kind == AntRuntimeKind.ControlPlane ? "control-plane component" : "deterministic service";
            }
            else if (kind == AntRuntimeKind.VisualScaffold)
            {
                implemented = false; available = false;
                reason = "not implemented (visual scaffold)";
            }
            else if (hasDeclaredContract && !AntRegistry.ExecutableRoleIds.Contains(id))
            {
                implemented = hasHandler;
                available = false;
                reason = !hasHandler ? "missing runtime handler"
                    : !SpecialistGateOpen(id) ? "disabled by configuration"
                    : "not yet registry-executable (canary stage incomplete)";
            }
            else // executable mission agent
            {
                implemented = hasHandler;
                available = hasHandler && role.Enabled;
                if (!hasHandler)
                {
                    reason = "missing runtime handler";
                    problems.Add($"Executable role '{id}' has NO runtime handler — kept unavailable (fail closed).");
                }
                // v3.8.23: a contract is required of EVERY mission agent, not just specialists.
                //
                // The `isSpecialist` qualifier used to make this check unreachable for the six core
                // ants, which is how they came to run with no declared surface at all — no supported
                // task types, no tool allowlist, no model requirement — while the runtime reported
                // them fully available. The most privileged role in the colony, the coder, was the
                // least specified, and nothing could see it because the check that would have said
                // so did not apply to it.
                else if (!hasContract)
                {
                    available = false; reason = "missing execution contract";
                    problems.Add($"Executable role '{id}' has no execution contract — kept unavailable (fail closed).");
                }
                else if (!role.Enabled) reason = "disabled in registry";
            }

            snap[id] = new RoleAvailability(id, kind, implemented, role.Enabled,
                PlannerEligible: available && kind == AntRuntimeKind.MissionAgent,
                RuntimeAvailable: available, UnavailabilityReason: available ? "" : reason);
        }

        lock (Lock) _snapshot = snap;
        return problems;
    }

    public static bool RuntimeAvailable(string roleId) =>
        Snapshot.TryGetValue(roleId ?? "", out var a) && a.RuntimeAvailable;
}
