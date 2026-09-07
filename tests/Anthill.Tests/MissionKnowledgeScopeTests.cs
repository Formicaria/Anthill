using Anthill.Core.Configuration;
using Anthill.Core.Orchestration;
using Anthill.SDK.Knowledge;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// WHICH KNOWLEDGE A MISSION MAY READ. v0.3.8.122.
///
/// The scope is ambient because <c>ITool.Run</c> takes arguments and nothing else, and an argument
/// would put the reach of a knowledge query in a model's hands. That design was shipped in `.121`
/// with the resolution missing: nothing ever entered a scope, so `KnowledgeScopeContext.Current` was
/// always `Unresolved` and every knowledge tool an ant dispatched refused. The console worked,
/// because it resolves a scope per request — which is how the gap hid. The surface a human used was
/// fine and the surface an agent used was inert.
///
/// These pin the resolution rules themselves, which is where the tenant boundary actually lives.
/// </summary>
public class MissionKnowledgeScopeTests
{
    private const string AnthillProject = "proj-anthill-a";
    private const string ForagerProject = "proj_ef42d498ae1e";

    private static KnowledgeSettings Configured(bool enabled = true, string? defaultProject = null) => new()
    {
        Enabled = enabled,
        Endpoint = "http://127.0.0.1:8790",
        ProjectMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AnthillProject] = ForagerProject,
        },
        DefaultProject = defaultProject ?? "",
    };

    private static Anthill.Core.Domain.Mission MissionFor(string? projectId) =>
        new() { Id = "m-1", Goal = "why did the launch date change", ProjectId = projectId };

    [Fact]
    public void AMappedProject_ResolvesToItsKnowledgeBase()
    {
        var scope = Queen.ResolveKnowledgeScope(MissionFor(AnthillProject), Configured());

        Assert.True(scope.IsQueryable);
        Assert.Equal(ForagerProject, scope.ProjectRef);
        Assert.Equal(KnowledgeScopeKind.Mission, scope.Kind);
        // The mission id travels with it so an audit of what was retrieved can name the run that asked.
        Assert.Equal("m-1", scope.MissionId);
        Assert.Equal(AnthillProject, scope.AnthillProjectId);
    }

    [Fact]
    public void TheMapIsCaseInsensitive_BecauseProjectIdsAreComparedThatWayEverywhereElse()
    {
        var scope = Queen.ResolveKnowledgeScope(MissionFor(AnthillProject.ToUpperInvariant()), Configured());
        Assert.True(scope.IsQueryable);
    }

    [Fact]
    public void AnUnmappedProject_RetrievesNothing()
    {
        var scope = Queen.ResolveKnowledgeScope(MissionFor("proj-anthill-b"), Configured());

        Assert.False(scope.IsQueryable);
        Assert.Equal(KnowledgeScopeKind.None, scope.Kind);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. `knowledge_default_project` exists so a console operator with no project
    /// context has somewhere to ask. A mission that borrowed it would be reading a knowledge base
    /// that is not its own — project A's mission answering from project B's documents, with full
    /// provenance, looking entirely correct. That is the single failure the scope model exists to
    /// prevent, and it would arrive disguised as a convenience.
    /// </summary>
    [Fact]
    public void AnUnmappedProject_DoesNotFallBackToTheDefault_EvenWhenOneIsConfigured()
    {
        var settings = Configured(defaultProject: "proj_someone_elses_knowledge");

        var scope = Queen.ResolveKnowledgeScope(MissionFor("proj-anthill-b"), settings);

        Assert.False(scope.IsQueryable);
        Assert.NotEqual("proj_someone_elses_knowledge", scope.ProjectRef);
    }

    [Fact]
    public void AMissionWithNoProject_RetrievesNothing()
    {
        // A direct API or CLI run. It has no tenant, so it has no knowledge — not the default one.
        var settings = Configured(defaultProject: "proj_someone_elses_knowledge");

        var scope = Queen.ResolveKnowledgeScope(MissionFor(null), settings);

        Assert.False(scope.IsQueryable);
    }

    [Fact]
    public void WithKnowledgeDisabled_EvenAMappedProjectResolvesToNothing()
    {
        var scope = Queen.ResolveKnowledgeScope(MissionFor(AnthillProject), Configured(enabled: false));

        Assert.False(scope.IsQueryable);
    }

    [Fact]
    public void AnEmptyMapping_IsTreatedAsUnmapped()
    {
        // A project present in the map with an empty value is a half-finished edit, not an intent
        // to grant access to nothing in particular.
        var settings = Configured() with
        {
            ProjectMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AnthillProject] = "",
            },
        };

        Assert.False(Queen.ResolveKnowledgeScope(MissionFor(AnthillProject), settings).IsQueryable);
    }

    [Fact]
    public void TwoMissionsInDifferentProjects_NeverShareACacheEntry()
    {
        var a = Queen.ResolveKnowledgeScope(MissionFor(AnthillProject), Configured());
        var b = Queen.ResolveKnowledgeScope(
            MissionFor("proj-anthill-c"),
            Configured() with
            {
                ProjectMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["proj-anthill-c"] = "proj_a_different_base",
                },
            });

        Assert.True(a.IsQueryable);
        Assert.True(b.IsQueryable);
        Assert.NotEqual(a.CacheKey, b.CacheKey);
    }

    /// <summary>
    /// The resolved scope is what a tool sees, and it must not leak past the mission that entered it.
    /// </summary>
    [Fact]
    public void EnteringTheScope_MakesItVisibleToTools_AndRestoresAfterwards()
    {
        Assert.False(KnowledgeScopeContext.HasScope);

        var scope = Queen.ResolveKnowledgeScope(MissionFor(AnthillProject), Configured());
        using (KnowledgeScopeContext.Enter(scope))
        {
            Assert.True(KnowledgeScopeContext.HasScope);
            Assert.Equal(ForagerProject, KnowledgeScopeContext.Current.ProjectRef);
        }

        Assert.False(KnowledgeScopeContext.HasScope);
    }

    [Fact]
    public void AnUnresolvedScope_EntersAsARefusalRatherThanAWildcard()
    {
        var scope = Queen.ResolveKnowledgeScope(MissionFor("proj-anthill-b"), Configured());

        using (KnowledgeScopeContext.Enter(scope))
        {
            // Entered, and still not queryable. "A scope was set" and "knowledge is reachable" are
            // different facts, and a tool must read the second.
            Assert.False(KnowledgeScopeContext.HasScope);
            Assert.False(KnowledgeScopeContext.Current.IsQueryable);
        }
    }
}
