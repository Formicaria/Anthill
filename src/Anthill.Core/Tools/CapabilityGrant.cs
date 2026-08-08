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
}
