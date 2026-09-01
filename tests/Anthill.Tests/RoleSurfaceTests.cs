using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// WHAT THE OPERATOR IS SHOWN IS WHAT THE COLONY HAS. v0.3.8.104.
///
/// THE DRIFT THIS CLOSES. `/ants/stats` carried a hand-written array of eight role names and had
/// disagreed with the registry three separate ways at once:
///
/// * it listed `strategist`, which is not a registered role at all — an operator reading the
///   Inspector saw it rendered beside six real ants as though it were one;
/// * it showed a provider and model for `file`, whose execution contract declares
///   `AllowsModelCalls: false` — a route for a call that role never makes;
/// * it omitted `tester`, `soldier`, `medic`, `archivist`, `ui_cartographer` and `scribe`, the six
///   specialists that ARE executable under the shipped profile.
///
/// A SECOND COPY OF THE TRUTH, MAINTAINED BY HAND, beside a computed one. `ToolInventoryTests`
/// already guards exactly this shape for tools and asserts BOTH directions — registered-but-absent
/// and claimed-but-unregistered — because a one-directional check catches half a drift. Nothing
/// guarded it for roles, which is why this one survived long enough to be wrong three ways.
///
/// The fix was to derive rather than to correct the list, and these tests exist because deriving is
/// undone by one person adding a name back.
/// </summary>
public class RoleSurfaceTests
{
    private static string Dashboard() => File.ReadAllText(Path.Combine(
        SourceText.RepoRoot(), "src", "Anthill.Api", "ApiHost.Dashboard.cs"));

    /// <summary>
    /// THE DASHBOARD DOES NOT CARRY ITS OWN ROLE LIST.
    ///
    /// Read as source shape rather than behaviour, for the same reason `CallSiteAuditTests` is: the
    /// behaviour is correct the day it is written and a literal array added later is invisible to
    /// every behavioural assertion. The pattern looks for a string array of three or more
    /// lowercase quoted names in this file — the shape the old list had.
    /// </summary>
    [Fact]
    public void TheDashboard_DoesNotHardcodeARoleList()
    {
        var arrays = Regex.Matches(Dashboard(),
                @"new\[\]\s*\{\s*(""[a-z_]+""\s*,\s*){2,}""[a-z_]+""\s*\}")
            .Select(m => m.Value.Replace("\n", " ").Trim())
            // Only arrays that actually name roles — the file legitimately lists other vocabularies.
            .Where(a => AntRegistry.Roles.Any(r => a.Contains($"\"{r.RoleId}\"", StringComparison.Ordinal)))
            .ToList();

        Assert.True(arrays.Count == 0,
            "ApiHost.Dashboard.cs contains a hand-written list of role names:\n  "
          + string.Join("\n  ", arrays)
          + "\n\nDerive it from AntRegistry or AntExecutionCatalog instead. The list this replaced "
          + "had drifted three ways at once — a name that was not a role, a model route for a role "
          + "that makes no model calls, and six executable roles missing.");
    }

    /// <summary>
    /// EVERY NON-ROLE MODEL ROUTE IS GENUINELY NOT A ROLE — or is one and should stop being an
    /// exception.
    ///
    /// `planner` and `strategist` are named exceptions because they call models and are not
    /// executable ants. If either ever becomes an executable role, the exception list would keep
    /// calling it an exception while the derivation also picked it up, and the Inspector would show
    /// it twice. This is the direction a hand-maintained list fails in that a one-way check misses.
    /// </summary>
    [Fact]
    public void EveryNonRoleModelRoute_IsNotAnExecutableRole()
    {
        var wrong = AntRegistry.NonRoleModelRoutes
            .Where(r => AntRegistry.ExecutableRoleIds.Contains(r))
            .ToList();

        Assert.True(wrong.Count == 0,
            "these are listed as non-role model routes and are executable roles: "
          + string.Join(", ", wrong)
          + ". Remove them from AntRegistry.NonRoleModelRoutes — the derivation already includes "
          + "every role whose contract declares a model call, so an executable role in that list is "
          + "displayed twice.");
    }

    /// <summary>
    /// AND EVERY ROLE THAT DECLARES A MODEL CALL IS A REAL ROLE. The other direction: a contract
    /// keyed by a name the registry does not have would put a phantom on the operator's screen,
    /// which is precisely what `strategist` did.
    /// </summary>
    [Fact]
    public void EveryModelCallingContract_NamesARegisteredRole()
    {
        var registered = AntRegistry.Roles.Select(r => r.RoleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var phantom = AntExecutionCatalog.Contracts
            .Where(c => c.Value.AllowsModelCalls)
            .Select(c => c.Key)
            .Where(r => !registered.Contains(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.True(phantom.Count == 0,
            "these execution contracts declare model calls under names the registry does not hold: "
          + string.Join(", ", phantom)
          + ". A contract for a role that does not exist is shown to the operator as a role that "
          + "does.");
    }

    /// <summary>
    /// THE COUNTS, PINNED WITH THEIR MEANINGS.
    ///
    /// Three different numbers get quoted interchangeably and they are not interchangeable: 25
    /// roles are REGISTERED, 12 role types are EXECUTABLE under the shipped profile, and only SIX
    /// of those twelve are executable by their own flag — the other six are specialists opened by
    /// canary gates, and thirteen roles are never executable at all (five control-plane, eight
    /// homelab). A document or a display quoting one of these as "the ants" is quoting a
    /// configuration rather than the system.
    /// </summary>
    [Fact]
    public void TheThreeRoleCounts_AreWhatTheyAreClaimedToBe()
    {
        Assert.Equal(25, AntRegistry.Roles.Count);

        var byFlag = AntRegistry.Roles.Count(r => r.Executable && r.Enabled);
        Assert.True(byFlag == 6,
            $"{byFlag} roles are executable by their own flag; six is the original set "
          + "(researcher, file, web, coder, builder, verifier). The other six of the twelve are "
          + "specialists opened by rollout gates, which is why 'twelve' describes a configuration.");

        Assert.True(AntRegistry.Roles.Count(r => !r.Executable) == 19,
            "nineteen registered roles are non-executable by flag: the six gated specialists, the "
          + "five control-plane roles, and the eight homelab ants that are visible by design and "
          + "never run.");
    }
}
