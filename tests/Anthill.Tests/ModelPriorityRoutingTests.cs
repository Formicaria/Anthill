using System.Text.Json;
using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.1 — every role is routable, and one model can outrank them all.
///
/// Two defects, one cause. The routing table seeded eight roles by hand while the colony ran twelve
/// ants, so archivist, file, medic, scribe, soldier, tester and ui_cartographer appeared nowhere in
/// it — and therefore nowhere in the console that renders it. They still ran, silently on the
/// fallback route, which is exactly why nobody noticed: nothing failed, there was simply no way to
/// point them anywhere else. The planner was routable but shared the same blind spot in practice,
/// and a colony whose planner model had gone missing fell back to a static plan with no console
/// remedy.
///
/// The priority model answers the other half. "I have a better model, use it everywhere" is ONE
/// decision, and making an operator express it by rewriting fourteen routes is how half of them end
/// up stale.
/// </summary>
public class ModelPriorityRoutingTests : IDisposable
{
    private readonly string _savedProvider;
    private readonly string _savedModel;

    public ModelPriorityRoutingTests()
    {
        AnthillRuntime.Initialize();
        _savedProvider = AnthillRuntime.ModelPriorityProvider;
        _savedModel = AnthillRuntime.ModelPriorityModel;
    }

    // Restored through the same public path the console uses, so the runtime gate and the persisted
    // config cannot be left disagreeing with each other by a test.
    public void Dispose() => SetPriority(_savedProvider, _savedModel);

    private static void SetPriority(string provider, string model) =>
        AnthillRuntime.ApplySettingsUpdate(new Dictionary<string, JsonElement>
        {
            ["model_priority_provider"] = JsonSerializer.SerializeToElement(provider),
            ["model_priority_model"] = JsonSerializer.SerializeToElement(model),
        });

    // ---- every ant is routable ------------------------------------------------------------------

    /// <summary>
    /// The list and the roster are two sides of one fact, and neither can be derived from the other
    /// safely — Configuration must not depend on Agents, and reading a registry property during
    /// ProjectConfig is an initialization-order trap this file has been bitten by before. So the
    /// pairing is checked here instead, which is the same deliberate friction the tool inventory
    /// uses: adding an ant means adding it in both places, and forgetting fails loudly.
    /// </summary>
    [Fact]
    public void EveryExecutableAnt_HasARoutableEntry()
    {
        var routable = new HashSet<string>(AnthillRuntime.RoutableRoles, StringComparer.OrdinalIgnoreCase);

        var missing = AntRegistry.ExecutableRoleIds
            .Where(r => !routable.Contains(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These ants can execute but have no entry in AnthillRuntime.RoutableRoles, so they are "
          + "invisible in the routing table and an operator cannot point them at a model: "
          + string.Join(", ", missing));
    }

    /// <summary>
    /// The planner is not an ant, and that is exactly why it was easy to miss. It makes model calls,
    /// so it must be routable — its absence from the console is what left a colony with a missing
    /// planner model silently falling back to a static plan.
    /// </summary>
    [Theory]
    [InlineData("planner")]
    [InlineData("strategist")]
    [InlineData("fallback")]
    public void OrchestrationRoles_AreRoutableToo(string role) =>
        Assert.Contains(role, AnthillRuntime.RoutableRoles);

    [Fact]
    public void EveryRoutableRole_IsSeededInTheRoutingTable()
    {
        var missing = AnthillRuntime.RoutableRoles
            .Where(r => !AnthillRuntime.ModelRouting.ContainsKey(r))
            .ToList();

        Assert.True(missing.Count == 0,
            "Seeded nowhere, so absent from /settings and from the console that renders it: "
          + string.Join(", ", missing));
    }

    // ---- the priority model ---------------------------------------------------------------------

    /// <summary>
    /// The default must be INERT. An unset priority has to leave per-role routing byte-for-byte as
    /// it was, or turning the feature on becomes the only state anyone understands and operators set
    /// it purely to know what they are getting.
    /// </summary>
    [Fact]
    public void WithNoPrioritySet_RoutingIsUnchanged()
    {
        SetPriority("", "");
        var router = new ModelRouter();

        Assert.False(AnthillRuntime.HasModelPriority);
        Assert.Equal(router.RoleRoute("coder"), router.GetRoute("coder"));
    }

    [Fact]
    public void APrioritySet_IsUsedByEveryRole()
    {
        SetPriority("ollama", "gemma3:27b");
        var router = new ModelRouter();

        foreach (var role in new[] { "planner", "coder", "medic", "ui_cartographer", "unknown_role" })
            Assert.Equal(("ollama", "gemma3:27b"), router.GetRoute(role));
    }

    /// <summary>
    /// Outranked, not erased. A role whose model was chosen deliberately — a bigger context window,
    /// a tool-calling model for an ant that needs one — must get that choice back when the priority
    /// is cleared, or setting one would quietly destroy every specialisation in the colony.
    /// </summary>
    [Fact]
    public void ARolesOwnRoute_SurvivesThePriorityAndComesBack()
    {
        var router = new ModelRouter();
        var own = router.RoleRoute("coder");

        SetPriority("ollama", "gemma3:27b");
        Assert.Equal(own, router.RoleRoute("coder"));       // still there underneath
        Assert.NotEqual(own, router.GetRoute("coder"));     // but not what is used

        SetPriority("", "");
        Assert.Equal(own, router.GetRoute("coder"));        // and it returns
    }

    /// <summary>
    /// Half a route is not a route. A provider with no model would otherwise be completed from
    /// defaults, sending every ant in the colony somewhere nobody chose — the loudest possible
    /// consequence for the quietest possible typo.
    /// </summary>
    [Theory]
    [InlineData("ollama", "")]
    [InlineData("", "gemma3:27b")]
    [InlineData("   ", "   ")]
    public void AnIncompletePriority_IsIgnored(string provider, string model)
    {
        SetPriority(provider, model);

        Assert.False(AnthillRuntime.HasModelPriority);
        Assert.Equal(new ModelRouter().RoleRoute("coder"), new ModelRouter().GetRoute("coder"));
    }

    /// <summary>
    /// Every routable role must be REACHABLE from the console, not merely present in the table.
    ///
    /// The caste grid is built from the ant roster, so planner, strategist and fallback — which make
    /// model calls and are not ants — had no control anywhere in the UI. That is the defect the
    /// operator reported: a colony whose planner model had gone missing fell back to a static plan
    /// with nowhere to repoint it. A route that exists in config and nowhere in the console is the
    /// same "shipped but unreachable" failure this project keeps paying for.
    /// </summary>
    [Fact]
    public void EveryRoleThatIsNotAnAnt_HasAConsoleControl()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.UI", "app.js"));

        // The full ROSTER, not ExecutableRoleIds. The latter is computed from live specialist gates,
        // so it shrinks and grows with configuration — a role whose canary gate happens to be closed
        // is still an ant with a card in the caste grid. Asserting against it would make this test
        // report six false failures depending on which gates were open when it ran, which is how a
        // guard becomes something people rerun instead of read.
        var ants = AntRegistry.Roles.Select(r => r.RoleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orchestration = AnthillRuntime.RoutableRoles.Where(r => !ants.Contains(r)).ToList();
        Assert.NotEmpty(orchestration);

        // Rendered from ORCHESTRATION_ROLES, so each must appear there by id.
        var block = Regex.Match(app, @"var ORCHESTRATION_ROLES = \[.*?\];", RegexOptions.Singleline).Value;
        Assert.NotEqual("", block);

        var missing = orchestration.Where(r => !block.Contains($"id:'{r}'")).ToList();
        Assert.True(missing.Count == 0,
            "These roles are routable but have no console control, so an operator cannot change the "
          + "model they call: " + string.Join(", ", missing));
    }

    /// <summary>
    /// v3.8.1 — a role gets model controls because it HAS A ROUTE, never because it happens to be
    /// executable right now.
    ///
    /// The caste grid hid the provider/model pair whenever a role's Executable flag was false, and
    /// that flag is computed from live specialist canary gates. So archivist, medic, scribe,
    /// soldier, tester and ui_cartographer rendered as cards with no way to set a model and stayed
    /// pinned to the seed default — in the reporting operator's colony, a model their Ollama host
    /// did not even serve. Executability decides whether a role DISPATCHES today; it says nothing
    /// about whether an operator may choose the model it calls when it does.
    ///
    /// Asserted on the condition itself rather than on rendered output, because the bug was the
    /// condition: any rule that consults executability here reintroduces it exactly.
    /// </summary>
    [Fact]
    public void ModelControls_AreGatedOnHavingARoute_NotOnExecutability()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.UI", "app.js"));

        var condition = Regex.Match(app, @"const modelField=\([^)]*\)").Value;
        Assert.NotEqual("", condition);

        Assert.Contains("hasRoute", condition);
        Assert.DoesNotContain("roleExecutable", condition);

        // And the route check must come from the ROUTE MAP, so every role the server routes gets a
        // control automatically as new ones are added.
        Assert.Contains("hasOwnProperty.call(routes,c)", app.Replace(" ", ""));
    }

    /// <summary>
    /// And the priority must be CLEARABLE from the console. A save that only ever wrote non-empty
    /// values would let an operator promote a model and never demote it — the setting would be a
    /// one-way door, which is exactly the shape of the Disable/Enable defect found in v3.7.2.
    /// </summary>
    [Fact]
    public void ThePriority_CanBeClearedFromTheConsole()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.UI", "app.js"));

        Assert.Contains("model_priority_provider", app);
        Assert.Contains("m.value ? p.value : ''", app);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Anthill.Api")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root");
        return dir!.FullName;
    }

    [Fact]
    public void ThePriority_IsOperatorEditableAndReported()
    {
        Assert.Contains("model_priority_provider", AnthillRuntime.EditableSettingKeys);
        Assert.Contains("model_priority_model", AnthillRuntime.EditableSettingKeys);

        SetPriority("ollama", "gemma3:27b");
        var snapshot = AnthillRuntime.SettingsSnapshot();

        Assert.Equal("gemma3:27b", snapshot["model_priority_model"]);
        // Reported rather than recomputed by the console, so what the colony believes about its own
        // precedence and what an operator is shown cannot drift apart.
        Assert.Equal(true, snapshot["model_priority_active"]);
    }
}
