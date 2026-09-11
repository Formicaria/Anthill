using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// BINDING A PROJECT REACHES THE THING THAT READS THE BINDING. v0.3.9.6.
///
/// THE DEFECT MADE **Bind** A DECORATION, and the visible half was the harmless one.
///
/// `POST /knowledge/project-map` edited `Config.KnowledgeProjectMap` and called `SaveConfig()`. That
/// is correct as far as it goes — the file on disk was right. What nothing did was RE-PROJECT:
/// `AnthillRuntime.Knowledge` is an immutable snapshot built once in `ProjectConfig`, and its
/// `ProjectMap` is a COPY. The live runtime therefore kept the old map until the next restart.
///
/// `/knowledge/status` reports the projected map, so the page said "no knowledge base bound"
/// immediately after reporting a successful bind. That is the half an operator can see, and it is
/// the half that does no damage. The other half: `Queen.ResolveKnowledgeScope` reads the SAME
/// projected map, so a freshly bound project genuinely retrieved nothing, and every mission under it
/// would have reported that correctly. A binding that was real on disk and absent from the running
/// colony — with no error anywhere, because both halves of the write worked.
///
/// `.96` fixed the model-route version of exactly this and wrote down the remedy: the mutate and the
/// save belong in one method under one lock. That method is `SetKnowledgeBinding`, and this test
/// asserts the property rather than the method — the WRITE must be visible to the READER a mission
/// uses, which is `KnowledgeSettings.ProjectRefFor`.
/// </summary>
public class KnowledgeBindingTests
{
    /// <summary>
    /// Bind, unbind, rebind — each time asking the resolver a mission would ask, not the config a
    /// mission never sees.
    /// </summary>
    [Fact]
    public void BindingAProject_IsVisibleToTheResolverImmediately()
    {
        AnthillRuntime.Initialize();

        var project = "test-proj-" + System.Guid.NewGuid().ToString("N")[..8];
        var before = AnthillRuntime.Knowledge.ProjectRefFor(project);
        Assert.Null(before);

        try
        {
            AnthillRuntime.SetKnowledgeBinding(project, "proj_abc123");

            // THE ASSERTION THAT MATTERS. Reading `Config.KnowledgeProjectMap` here would have
            // passed against the defect — the config was always correct. A mission resolves its
            // scope through the projected settings, so that is what is asked.
            Assert.Equal("proj_abc123", AnthillRuntime.Knowledge.ProjectRefFor(project));

            // Rebinding moves it rather than adding a second answer.
            AnthillRuntime.SetKnowledgeBinding(project, "proj_xyz789");
            Assert.Equal("proj_xyz789", AnthillRuntime.Knowledge.ProjectRefFor(project));

            // And unbinding REFUSES, rather than falling back to the default — the property the
            // scope model exists for, asserted here because this is the write path that could
            // quietly break it.
            AnthillRuntime.SetKnowledgeBinding(project, "");
            Assert.Null(AnthillRuntime.Knowledge.ProjectRefFor(project));
        }
        finally
        {
            AnthillRuntime.SetKnowledgeBinding(project, "");
        }
    }

    /// <summary>
    /// AND THE CONFIG AGREES WITH THE RUNTIME, in both directions. The defect was the two disagreeing
    /// while each was internally consistent, so a guard that checked only one of them would have
    /// passed — which is precisely what every existing test did.
    /// </summary>
    [Fact]
    public void TheLiveSettingsAndThePersistedConfig_NeverDisagree()
    {
        AnthillRuntime.Initialize();

        var project = "test-proj-" + System.Guid.NewGuid().ToString("N")[..8];
        try
        {
            AnthillRuntime.SetKnowledgeBinding(project, "proj_pair");

            Assert.True(AnthillRuntime.Config.KnowledgeProjectMap.TryGetValue(project, out var onDisk));
            Assert.Equal("proj_pair", onDisk);
            Assert.Equal(onDisk, AnthillRuntime.Knowledge.ProjectRefFor(project));

            AnthillRuntime.SetKnowledgeBinding(project, "");

            Assert.False(AnthillRuntime.Config.KnowledgeProjectMap.ContainsKey(project));
            Assert.Null(AnthillRuntime.Knowledge.ProjectRefFor(project));
        }
        finally
        {
            AnthillRuntime.SetKnowledgeBinding(project, "");
        }
    }

    /// <summary>
    /// THE MAP HAS ONE WRITER. The defect could only exist because the route reached past the
    /// runtime into the config; any future handler that does the same reintroduces it exactly.
    ///
    /// A source guard, because the property is "nobody else writes this" and there is no runtime
    /// observation of a thing not happening. `AnthillRuntime.cs` is exempt: it is where the one
    /// writer lives.
    /// </summary>
    [Fact]
    public void OnlyTheRuntimeItself_WritesTheProjectMap()
    {
        var offenders = new List<string>();
        var root = Path.Combine(SourceText.RepoRoot(), "src");

        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "AnthillRuntime.cs") continue;
            var code = SourceText.CodeOnly(File.ReadAllText(file));

            // An assignment into the map, or a removal from it, from outside the one writer.
            if (Regex.IsMatch(code, @"KnowledgeProjectMap\s*\[[^\]]*\]\s*=")
             || Regex.IsMatch(code, @"KnowledgeProjectMap\s*\.\s*(Remove|Clear|Add)\s*\("))
            {
                offenders.Add(Path.GetRelativePath(SourceText.RepoRoot(), file).Replace('\\', '/'));
            }
        }

        Assert.True(offenders.Count == 0,
            "These files write the knowledge project map directly instead of through "
          + "`AnthillRuntime.SetKnowledgeBinding`. That writes the FILE and leaves the LIVE settings "
          + "stale, because `Knowledge.ProjectMap` is a copy taken at projection — which is how Bind "
          + "came to succeed on disk while the colony went on retrieving nothing:\n  "
          + string.Join("\n  ", offenders));
    }
}
