using Anthill.SDK.Contracts;

namespace Anthill.Core.Tools;

/// <summary>
/// What this RUN can actually provide, as distinct from what a role declares it needs. v3.8.25.
///
/// <see cref="AntExecutionContract.RequiredCapabilities"/> has existed since v2.19.0 and nothing has
/// ever answered the other half of the question. <see cref="ToolExecutionContext"/> — the
/// capability-aware authorization path — has been in the tree with a test call site and NO
/// production one since it was written, for the simple reason that nobody could construct one:
/// <c>GrantedCapabilities</c> had no source.
///
/// This is that source, and the shape of it is the load-bearing decision. The grant is derived from
/// WHAT THE COMPOSITION ROOT ACTUALLY BUILT — which tools reached the registry, whether a reasoning
/// provider exists, what the run's options permit. It is deliberately NOT derived from the contracts
/// themselves: granting each role exactly what it declares it needs produces a check that can never
/// fail, which is a call site in the shape of a gate and the precise defect this project has now
/// found seven times.
///
/// The check that results is worth having. A role requiring <c>network.http.public</c> in a colony
/// built with web search off is currently discovered as "Tool not found or not registered" at
/// dispatch — a message about a missing tool, for a missing capability. Now it is refused with the
/// reason.
/// </summary>
public static class CapabilityGrant
{
    /// <summary>
    /// Resolve the capabilities a run can provide.
    /// </summary>
    /// <param name="registeredTools">The tool names that actually reached the registry — module
    /// contributions included, since a colony built without <c>Anthill.Modules.Tools</c> genuinely
    /// cannot read a file and should say so.</param>
    /// <param name="modelAvailable">True when a reasoning provider was composed in. The core runs
    /// without one by design (v3.8.5), and in that colony no role can invoke a model.</param>
    /// <param name="webSearchEnabled">The run's own switch, checked ALONGSIDE the tool's presence:
    /// the tool can be registered while the gate is closed, and the capability follows the gate.</param>
    public static IReadOnlySet<string> Resolve(
        IReadOnlySet<string> registeredTools, bool modelAvailable, bool webSearchEnabled)
    {
        var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Has(params string[] names) => names.Any(registeredTools.Contains);

        if (Has("read_text_file", "list_directory")) granted.Add(Capability.RepoRead);
        if (Has("search_workspace", "repository_index")) granted.Add(Capability.RepoSearch);
        if (Has("run_allowlisted_check")) granted.Add(Capability.ProcessExecuteReadonly);
        if (webSearchEnabled && Has("web_search")) granted.Add(Capability.NetworkHttpPublic);
        if (modelAvailable) granted.Add(Capability.ModelInvoke);

        // PROPOSING a patch needs nothing from the environment — it produces a record, and the
        // Queen's materialisation and the operator's approval stand between that record and the
        // tree. Granted unconditionally, and named here rather than left implicit so the contrast
        // with the line below is visible.
        granted.Add(Capability.RepoPatchPropose);

        // repo.patch.apply and repo.write.sandbox are NEVER granted here. No mission agent applies a
        // patch; that is the approval pipeline's alone, and the two capabilities exist as separate
        // names precisely so this function can grant one and withhold the other.

        return granted;
    }

    /// <summary>
    /// A grant containing everything the twelve contracts require, for callers that legitimately
    /// have no run to resolve against — the API's projection of "what could this role ever call",
    /// and tests that are not about capability resolution.
    ///
    /// Named FULL rather than DEFAULT on purpose. It is the permissive answer, and a caller reaching
    /// for it should have to notice that.
    /// </summary>
    public static IReadOnlySet<string> Full =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Capability.RepoRead, Capability.RepoSearch, Capability.ProcessExecuteReadonly,
            Capability.NetworkHttpPublic, Capability.ModelInvoke, Capability.RepoPatchPropose,
        };

    /// <summary>
    /// Capability names that EXIST and that no colony role may ever hold, each with the reason.
    /// v0.3.8.87.
    ///
    /// Seven of the fourteen names in <see cref="Capability"/> are granted by nothing and required by
    /// nobody. That is not automatically wrong — <c>repo.patch.apply</c> is withheld on purpose and
    /// the comment above says so — but "deliberately withheld" and "nobody got round to wiring it"
    /// look identical from outside, and the difference is the whole reason a permission vocabulary is
    /// worth reading. v0.3.8.86 found the same shape in the event vocabulary, where two constants
    /// nothing published were NEAR-MISSES of real event names and a subscriber filtering on either
    /// matched nothing forever.
    ///
    /// So the set is enumerated rather than inferred. <c>CapabilityDeclarationTests</c> requires every
    /// declared capability to be granted, required, or named here — which means a new capability
    /// cannot join the vocabulary and quietly reach nobody, and a name listed here cannot start being
    /// granted without someone removing it from this list and reading the reason first.
    ///
    /// This is a REGISTER, never a gate. Nothing consults it to decide anything at runtime; refusal
    /// comes from <see cref="Resolve"/> not granting the capability, exactly as before.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DeliberatelyUngranted { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Capability.RepoPatchApply] =
                "no mission agent applies a patch. Applying is the approval pipeline's alone, and this "
              + "name exists separately from repo.patch.propose precisely so Resolve can grant one and "
              + "withhold the other.",
            [Capability.RepoWriteSandbox] =
                "no role writes to a sandbox today. Held back rather than deleted because sandboxed "
              + "execution is a runtime switch (AnthillRuntime.EnableSandboxExecution) and the "
              + "capability is what a future grant would name. Until v0.3.8.87 ToolCatalog declared "
              + "the BUILDER as requiring it — a requirement nothing could ever satisfy, in a catalog "
              + "nothing enforced.",
            [Capability.NetworkHttpHomelab] =
                "homelab HTTP belongs to the Homelab module's action runners, which authorize through "
              + "ActionExecutor rather than through a colony role's capability grant.",
            [Capability.ProxmoxRead] = "Proxmox is reached by the Homelab module, not by a colony role.",
            [Capability.ProxmoxVmStart] = "as proxmox.read — module surface, not a role capability.",
            [Capability.ProxmoxVmStop] = "as proxmox.read — module surface, not a role capability.",
            [Capability.ProxmoxSnapshotCreate] = "as proxmox.read — module surface, not a role capability.",
            [Capability.CredentialUse] =
                "no role is granted credential use. Secrets reach a module through its own configured "
              + "client, never through a mission agent's grant set.",
        };
}
