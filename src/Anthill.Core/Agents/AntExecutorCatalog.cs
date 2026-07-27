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

public static class AntExecutorCatalog
{
    private static readonly object Lock = new();
    private static IReadOnlyDictionary<string, RoleAvailability> _snapshot =
        new Dictionary<string, RoleAvailability>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, RoleAvailability> Snapshot { get { lock (Lock) return _snapshot; } }

    /// <summary>
    /// Specialist roles arriving via the framework (Stage D canaries) and their gates.
    ///
    /// v2.22.0: THREE conditions, all required — the master switch, the activation tier's ceiling,
    /// and the role's own rollout flag. The tier can only ever narrow: a role whose flag is off
    /// stays off at `full`, so raising the tier never switches anything on by itself and every
    /// existing rollout gate remains exactly as binding as before.
    /// </summary>
    public static bool SpecialistGateOpen(string roleId) =>
        AnthillRuntime.EnableSpecialistAntExecution
        && ActivationTiers.Admits(AnthillRuntime.ActivationTier, roleId)
        && roleId switch
        {
            "tester" => AnthillRuntime.EnableTesterAnt,
            "soldier" => AnthillRuntime.EnableSoldierAnt,
            "medic" => AnthillRuntime.EnableMedicAnt,
            "archivist" => AnthillRuntime.EnableArchivistAnt,
            "ui_cartographer" => AnthillRuntime.EnableUiCartographerAnt,
            "scribe" => AnthillRuntime.EnableScribeAnt,
            _ => false,
        };

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
            var isSpecialist = AntExecutionCatalog.Contracts.ContainsKey(id);

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
            else if (isSpecialist && !AntRegistry.ExecutableRoleIds.Contains(id))
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
                else if (isSpecialist && !hasContract)
                {
                    available = false; reason = "missing execution contract";
                    problems.Add($"Executable specialist '{id}' has no execution contract — kept unavailable.");
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
