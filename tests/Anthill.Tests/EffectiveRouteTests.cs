using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// What a reader is told about a route must be what a caller would use. v0.3.8.41.
///
/// FOUND ON A RUNNING COLONY, not in review. Booting this project's own machine printed seven of
/// these:
///
/// <code>
/// [model-fitness] role 'coder' is routed to ollama:, which is missing: structured output; reasoning
/// </code>
///
/// Note `ollama:` with nothing after the colon — and `llama3.1:8b` installed, pulled, and serving
/// requests. The report was grading the capabilities of a model whose name is the empty string.
/// `builder` came back FIT from that same empty string, because its only requirement is a context
/// window and an unknown window is deliberately not reported as too small. So the output was a
/// function of each role's REQUIREMENTS and nothing else: it would have printed the identical seven
/// lines on a host with no Ollama at all, and on a host with the perfect model.
///
/// THE CAUSE was two halves of one decision living apart. `ResolveRoute` answers "what is
/// configured", which v3.8.33 made legitimately empty when nobody has chosen a model. `GetClient`
/// then resolved the real model on the call path — catalogue default, then the sole-installed-model
/// rule. Everything that DISPLAYED a route read the first; everything that CALLED a model used the
/// second; nothing made them agree.
///
/// This is the eleventh instance of this repository's signature defect — a check answering a
/// question adjacent to the one asked, and passing — and the second time in THIS report
/// specifically. v3.8.2 fixed it when fitness read the declared capability table instead of the
/// discovered one. It came back through a different door as soon as an empty model became legal,
/// which is the argument for a shared resolution rather than a second careful reader.
/// </summary>
// Serialised with the other tests that mutate model settings. `WithNoConfiguredModel` writes
// `AnthillRuntime.OllamaModel`, which is process-global, and `RouteModelDefaultTests` writes it
// through the settings path — two classes doing that in parallel is a flake nobody can reproduce.
[Collection("specialist-gates")]
public class EffectiveRouteTests
{
    /// <summary>A host with exactly these models installed, and no network.</summary>
    private static LocalModelResolver.ModelLister Installed(params string[] models) => _ => models;

    /// <summary>A host that cannot be reached — a throw, which the resolver distinguishes from
    /// "installed nothing". The two have different remedies.</summary>
    private static LocalModelResolver.ModelLister Unreachable =>
        _ => throw new HttpRequestException("connection refused");

    private static ModelRouter Router(LocalModelResolver.ModelLister lister) =>
        new(memory: null, breaker: null, modelLister: lister);

    /// <summary>
    /// Run <paramref name="body"/> with NO model configured anywhere.
    ///
    /// Both the live static AND `Config` are cleared, and that is not belt-and-braces. The first
    /// version of this set only `AnthillRuntime.OllamaModel`, and the test process's own
    /// `.anthill/config.json` carries `"ollama_model": "llama3.1:8b"` — so any call that re-runs
    /// `ProjectConfig` puts the tag straight back from `Config`, and `RoleRoute` then reports it as
    /// a deliberate operator choice. The symptom was the sole-installed-model test resolving to a
    /// model that was not in the list the test supplied.
    ///
    /// This is the same trap `RouteModelDefaultTests` documents from the other direction: writing a
    /// route ends in `ProjectConfig`, which re-reads this field from `Config`, erasing the
    /// assignment on the line before.
    ///
    /// AND THE ROUTES THEMSELVES, which is the part that actually bit. `ProjectConfig` pre-populates
    /// <c>ModelRouting[role]</c> for every routable role with the model as it stood AT INIT TIME:
    ///
    /// <code>foreach (var role in RoutableRoles) ModelRouting[role] = defaultRoute();</code>
    ///
    /// So `RoleRoute` reads a baked dictionary, not the live static, and clearing
    /// <c>OllamaModel</c> afterwards changes nothing at all. That is also true in production — an
    /// operator changing the model in Settings only takes effect because `ApplySettingsUpdate` runs
    /// `ProjectConfig` again and rebuilds these entries.
    /// </summary>
    private static T WithNoConfiguredModel<T>(Func<T> body)
    {
        AnthillRuntime.Initialize();   // so Config exists and cannot re-initialise mid-test
        var previousLive = AnthillRuntime.OllamaModel;
        var previousConfig = AnthillRuntime.Config.OllamaModel;
        var previousRoutes = AnthillRuntime.ModelRouting.ToDictionary(
            kv => kv.Key, kv => new Dictionary<string, string>(kv.Value), StringComparer.OrdinalIgnoreCase);
        try
        {
            AnthillRuntime.Config.OllamaModel = "";
            AnthillRuntime.OllamaModel = "";
            foreach (var route in AnthillRuntime.ModelRouting.Values) route["model"] = "";
            return body();
        }
        finally
        {
            AnthillRuntime.Config.OllamaModel = previousConfig;
            AnthillRuntime.OllamaModel = previousLive;
            AnthillRuntime.ModelRouting.Clear();
            foreach (var (role, route) in previousRoutes) AnthillRuntime.ModelRouting[role] = route;
        }
    }

    // -------------------------------------------------------------------------------------------
    // The resolution itself
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE regression. With no model configured and exactly one installed, the effective model is
    /// the installed one — not the empty string the configuration holds.
    /// </summary>
    [Fact]
    public void WithNoModelConfigured_TheEffectiveModelIsTheSoleInstalledOne() =>
        WithNoConfiguredModel(() =>
        {
            var route = Router(Installed("llama3.1:8b"))
                .ResolveEffectiveModel(AnthillRuntime.DefaultModelProvider, "");

            Assert.True(route.Resolved, route.Reason);
            Assert.Equal("llama3.1:8b", route.Model);
            return 0;
        });

    /// <summary>A configured model is honoured without asking the host anything.</summary>
    [Fact]
    public void AConfiguredModel_IsUsedVerbatim()
    {
        var route = Router(Unreachable)
            .ResolveEffectiveModel(AnthillRuntime.DefaultModelProvider, "mistral:7b");

        Assert.True(route.Resolved);
        Assert.Equal("mistral:7b", route.Model);
    }

    /// <summary>
    /// Several installed and none chosen REFUSES, with a reason. Picking one for the operator would
    /// be a guess that silently changes which model their colony runs on between hosts.
    /// </summary>
    [Fact]
    public void WithSeveralInstalledAndNoneChosen_TheRouteRefusesWithAReason() =>
        WithNoConfiguredModel(() =>
        {
            var route = Router(Installed("llama3.1:8b", "mistral:7b"))
                .ResolveEffectiveModel(AnthillRuntime.DefaultModelProvider, "");

            Assert.False(route.Resolved);
            Assert.False(string.IsNullOrWhiteSpace(route.Reason));
            Assert.Equal("", route.Model);
            return 0;
        });

    /// <summary>An unreachable host refuses too, and says something different from "none installed".</summary>
    [Fact]
    public void AnUnreachableHost_RefusesDistinctly() =>
        WithNoConfiguredModel(() =>
        {
            var unreachable = Router(Unreachable).ResolveEffectiveModel(AnthillRuntime.DefaultModelProvider, "");
            var empty = Router(Installed()).ResolveEffectiveModel(AnthillRuntime.DefaultModelProvider, "");

            Assert.False(unreachable.Resolved);
            Assert.False(empty.Resolved);
            Assert.NotEqual(unreachable.Reason, empty.Reason);
            return 0;
        });

    // -------------------------------------------------------------------------------------------
    // The report reads the effective route
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The fitness report grades the model that would actually run.
    ///
    /// This is the assertion that fails against the previous implementation: with no model
    /// configured it reported every role against `""`, so `coder` was unfit for lacking structured
    /// output on a host whose only model has it.
    /// </summary>
    [Fact]
    public void TheFitnessReport_GradesTheEffectiveModel_NotTheConfiguredOne() =>
        WithNoConfiguredModel(() =>
        {
            var report = AntModelFitness.CheckAll(
                Router(Installed("llama3.1:8b")), AntExecutionCatalog.Contracts);

            Assert.NotEmpty(report);
            Assert.All(report, f =>
            {
                Assert.Null(f.Unresolved);
                Assert.Equal("llama3.1:8b", f.Model);
            });
            return 0;
        });

    /// <summary>
    /// An unresolvable route reports as UNRESOLVED, not as a model missing capabilities.
    ///
    /// The distinction is the whole point. "Choose a model" and "your model cannot do structured
    /// output" are different problems, and the second is a false statement when the first is true —
    /// it sends an operator looking for a better model when they have not picked one at all.
    /// </summary>
    [Fact]
    public void AnUnresolvableRoute_IsReportedAsUnresolved_NotAsMissingCapabilities() =>
        WithNoConfiguredModel(() =>
        {
            var report = AntModelFitness.CheckAll(
                Router(Installed("a:1", "b:2")), AntExecutionCatalog.Contracts);

            Assert.NotEmpty(report);
            Assert.All(report, f =>
            {
                Assert.NotNull(f.Unresolved);
                Assert.Empty(f.Unmet);          // nothing was measured, so nothing is claimed
                Assert.False(f.Fit);            // and it is certainly not fit
            });
            return 0;
        });

    /// <summary>
    /// `Fit` means measured AND adequate. An unresolved route must never read as fit, or the
    /// unfit-role count on the status chip would report a colony with no model as healthy.
    /// </summary>
    [Fact]
    public void AnUnresolvedRoute_IsNeverFit()
    {
        var unresolved = new ModelFitness("coder", "ollama", "", Array.Empty<string>(), "no model chosen");
        var fit = new ModelFitness("coder", "ollama", "llama3.1:8b", Array.Empty<string>());

        Assert.False(unresolved.Fit);
        Assert.True(fit.Fit);
    }

    // -------------------------------------------------------------------------------------------
    // One resolution, not two
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The router resolves a model in exactly ONE place.
    ///
    /// The defect was two implementations of one decision drifting apart, so the guard is against
    /// there being two again. `LocalModelResolver.Resolve` may be called from
    /// `ResolveEffectiveModel` and nowhere else in the router — every other reader goes through it.
    /// </summary>
    [Fact]
    public void TheRouter_ResolvesALocalModelInExactlyOnePlace()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Models", "ModelRouter.cs")));

        var calls = System.Text.RegularExpressions.Regex
            .Matches(source, @"LocalModelResolver\.Resolve\s*\(").Count;

        Assert.True(calls == 1,
            $"ModelRouter calls LocalModelResolver.Resolve {calls} times. Two call sites is how the "
          + "reported model and the called model came to disagree in the first place — every reader "
          + "must go through ResolveEffectiveModel.");
    }

    /// <summary>
    /// And the fitness report does not reach for the configured model behind the resolution's back.
    /// </summary>
    [Fact]
    public void TheFitnessReport_DoesNotGradeTheConfiguredModel()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "AntModelFitness.cs")));

        Assert.Contains("ResolveEffectiveModel", source, StringComparison.Ordinal);
        // CapabilitiesFor must be asked about the RESOLVED model.
        Assert.DoesNotContain("CapabilitiesFor(provider, configured)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CapabilitiesFor(provider, model)", source, StringComparison.Ordinal);
    }
}
