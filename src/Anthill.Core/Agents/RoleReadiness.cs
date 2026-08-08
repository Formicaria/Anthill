using Anthill.Core.Configuration;

namespace Anthill.Core.Agents;

/// <summary>One role's readiness, and when it is not ready, the single binding reason.</summary>
public sealed record RoleReadinessRow(
    string RoleId,
    bool Ready,
    string BlockedReason,
    string SchedulingMode,
    bool HandlerPresent,
    string ContractVersion,
    IReadOnlyList<string> DeclaredTools,
    IReadOnlyList<string> UnregisteredTools,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> UngrantedCapabilities,
    bool Gated,
    RoleGateStatus GateStatus,
    bool AdmittedByTier,
    bool GateOpen,
    bool RuntimeAvailable);

/// <summary>
/// Per-role readiness, as a function. v3.8.32.
///
/// This logic shipped in v3.8.26 as a lambda inside the <c>/colony/registry</c> route handler, and
/// that placement is why it was WRONG for half the roster for six releases with nobody noticing:
/// nothing could call it without standing up an HTTP host, so nothing ever did.
///
/// The defect it carried: the ladder ran over all twelve contract keys and asked two questions that
/// only mean something about a GATED specialist. <c>ActivationTiers.Admits</c> returns false for a
/// core ant at the default <c>core</c> tier, and the old <c>SpecialistGateOpen</c> had no switch arm
/// for one. So researcher, web, file, coder, builder and verifier — the six ants that are always on
/// — were reported <c>ready: false</c>, blocked by flags that do not exist, on the exact surface an
/// operator reads to decide what to switch on.
///
/// Extracted here so the ladder can be exercised over the whole roster by a test, which is the only
/// reason the bug is now visible.
/// </summary>
public static class RoleReadiness
{
    /// <summary>
    /// Evaluate every contracted role against what THIS run actually provides.
    /// </summary>
    /// <param name="registeredTools">Tool names the registry really holds — not what contracts ask
    /// for. A readiness check fed the contracts' own declarations could never fail.</param>
    /// <param name="grantedCapabilities">What the composition root could grant, or null when the
    /// caller has no grant to offer (in which case capabilities are not judged).</param>
    public static List<RoleReadinessRow> ForAllRoles(
        IReadOnlyCollection<string> registeredTools,
        IReadOnlySet<string>? grantedCapabilities)
    {
        var rows = new List<RoleReadinessRow>();

        foreach (var role in AntExecutionCatalog.Contracts.Keys.OrderBy(r => r, StringComparer.Ordinal))
        {
            var contract = AntExecutionCatalog.ContractFor(role)!;
            var availability = AntExecutorCatalog.Snapshot.GetValueOrDefault(role);

            var missingTools = contract.AllowedTools
                .Where(t => !registeredTools.Contains(t, StringComparer.OrdinalIgnoreCase))
                .OrderBy(t => t, StringComparer.Ordinal).ToList();

            var ungranted = grantedCapabilities is null
                ? new List<string>()
                : contract.RequiredCapabilities.Where(c => !grantedCapabilities.Contains(c))
                    .OrderBy(c => c, StringComparer.Ordinal).ToList();

            var gate = AntExecutorCatalog.GateStatusOf(role);

            // Fail closed and report the FIRST binding reason, in the order the runtime would hit
            // them. A list of every problem reads as a crisis; the one actually stopping it reads as
            // a next step.
            var gateReason = gate switch
            {
                RoleGateStatus.ClosedByMasterSwitch => "specialist execution is disabled colony-wide",
                RoleGateStatus.ClosedByTier =>
                    $"activation tier '{ActivationTiers.Name(AnthillRuntime.ActivationTier)}' does not admit this role",
                RoleGateStatus.ClosedByRolloutFlag => "this role's own rollout flag is off",
                // NotGated and Open both mean "the gate is not what is stopping this role".
                _ => "",
            };

            var blocked =
                availability is null ? "role is not in the runtime snapshot"
                : !availability.Implemented ? "no runtime handler"
                : gateReason.Length > 0 ? gateReason
                : missingTools.Count > 0 ? $"declared tools are not registered: {string.Join(", ", missingTools)}"
                : ungranted.Count > 0 ? $"this run cannot grant: {string.Join(", ", ungranted)}"
                : availability.RuntimeAvailable ? "" : availability.UnavailabilityReason;

            rows.Add(new RoleReadinessRow(
                RoleId: role,
                Ready: blocked.Length == 0,
                BlockedReason: blocked,
                SchedulingMode: contract.Scheduling.ToString(),
                HandlerPresent: availability?.Implemented ?? false,
                ContractVersion: contract.Version,
                DeclaredTools: contract.AllowedTools.OrderBy(t => t, StringComparer.Ordinal).ToList(),
                UnregisteredTools: missingTools,
                RequiredCapabilities: contract.RequiredCapabilities.OrderBy(c => c, StringComparer.Ordinal).ToList(),
                UngrantedCapabilities: ungranted,
                Gated: gate != RoleGateStatus.NotGated,
                GateStatus: gate,
                // For an ungated core ant both of these are true by definition: no tier restricts it
                // and no flag governs it. Reporting the raw predicates here is what made the UI say
                // "not admitted" about ants that are always admitted.
                AdmittedByTier: gate == RoleGateStatus.NotGated
                                || ActivationTiers.Admits(AnthillRuntime.ActivationTier, role),
                GateOpen: gate is RoleGateStatus.NotGated or RoleGateStatus.Open,
                RuntimeAvailable: availability?.RuntimeAvailable ?? false));
        }

        return rows;
    }
}
