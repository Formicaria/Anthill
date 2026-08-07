using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Anthill.Modules.Tools;
using Anthill.SDK.Events;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.16 — phase 5c step 4: the six tools that act on the machine, as a module.
///
/// The property worth testing is not that they still work — <c>ToolAuthorizationTests</c>,
/// <c>ToolRuntimeOptionsTests</c> and <c>UiCartographerAntTests</c> already exercise the behaviour,
/// unchanged, through the module. What is new and could silently rot is the COMPOSITION: the colony
/// gates tools twice on purpose, and the first of those two gates moved out of
/// <c>Queen.BuildToolRegistry</c> into <c>ToolsModule.Register</c>.
///
/// If the module registered everything unconditionally and left the call-time check to catch it,
/// every existing test would still pass and the two gates would have quietly become one.
/// </summary>
public class ToolsModuleTests
{
    /// <summary>
    /// Gates passed in explicitly rather than by flipping <c>AnthillRuntime</c> statics, and the
    /// first draft of this file learned why the hard way: <c>SqliteMemory</c>'s schema setup calls
    /// <c>AnthillRuntime.Initialize()</c>, which projects config over those statics. A test that set
    /// a flag and THEN built a colony had its flag quietly reverted before the module ever read it —
    /// so the module looked like it was ignoring a gate it was honouring exactly.
    /// </summary>
    private sealed class Gates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled { get; init; }
        public bool FileWritingEnabled { get; init; }
        public bool ShellToolEnabled { get; init; }
        public bool WebSearchEnabled { get; init; }
        public bool PatchApplicationEnabled { get; init; }
        public IReadOnlySet<string> WebSearchKeywords { get; } = new HashSet<string>();
        public IReadOnlySet<string> PatchAllowedSuffixes { get; } = new HashSet<string> { ".cs" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; } = new HashSet<string> { ".db" };
        public IReadOnlySet<string> BlockedPathParts { get; } = new HashSet<string> { ".git" };
        public string ScriptDirectory => ".";
        public string BackupDirectory => "data/backups";
    }

    /// <summary>
    /// A real <see cref="ModuleHost"/> rather than a fake context, because the buffering is part of
    /// what is being checked: a module registers into a colony that has no tool registry yet.
    /// </summary>
    private static IReadOnlyList<string> Contributed(IToolRuntimeOptions gates)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"anthill-tools-{Guid.NewGuid():N}.db");
        try
        {
            using var memory = new SqliteMemory(dbPath);
            var host = new ModuleHost(memory, NullEventBus.Instance);
            host.Load(new ToolsModule(new WorkspacePathGuard(), gates));
            return host.ContributedTools.Select(t => t.Name).ToList();
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort; the temp dir is swept anyway */ }
        }
    }

    /// <summary>
    /// The three tools that carry their own gate are registered whether or not it is open, exactly
    /// as the core registered them. A tool that is present and refusing is a different — and more
    /// diagnosable — operator problem than a tool that is absent.
    /// </summary>
    [Fact]
    public void TheSelfGatingTools_AreAlwaysOffered()
    {
        var names = Contributed(new Gates { FileToolsEnabled = false, FileWritingEnabled = false });

        Assert.Contains("web_search", names);
        Assert.Contains("shell_command", names);
        Assert.Contains("apply_patch", names);
    }

    /// <summary>
    /// The first of the colony's two gates, in its new home. With file tools off, the module must
    /// not offer them at all — not offer them and rely on the call-time re-check.
    /// </summary>
    [Fact]
    public void WithFileToolsOff_TheFileReadersAreNotEvenRegistered()
    {
        var names = Contributed(new Gates { FileToolsEnabled = false });

        Assert.DoesNotContain("list_directory", names);
        Assert.DoesNotContain("read_text_file", names);
    }

    /// <summary>Writing is a separate, narrower gate, and it was separate in the core too.</summary>
    [Fact]
    public void WithFileToolsOn_ButWritingOff_OnlyTheReadersArrive()
    {
        var names = Contributed(new Gates { FileToolsEnabled = true, FileWritingEnabled = false });

        Assert.Contains("list_directory", names);
        Assert.Contains("read_text_file", names);
        Assert.DoesNotContain("write_text_file", names);
    }

    [Fact]
    public void WithEverythingOn_AllSixArrive()
    {
        var names = Contributed(new Gates { FileToolsEnabled = true, FileWritingEnabled = true });

        Assert.Equal(6, names.Count);
    }

    /// <summary>
    /// Every name the module contributes must be one the inventory already knows. A module that
    /// could introduce a tool name the core has never heard of would be a module that can widen the
    /// colony's vocabulary without review — and <c>ToolAuthorization</c>'s tables are all closed
    /// lists compiled into the build, so such a tool would be denied to every role anyway.
    /// </summary>
    [Fact]
    public void EveryContributedName_IsInTheInventory()
    {
        var unknown = Contributed(new Gates { FileToolsEnabled = true, FileWritingEnabled = true })
            .Where(n => !ToolInventory.Implemented.Contains(n)).ToList();

        Assert.True(unknown.Count == 0,
            "The tools module contributed names the core's inventory does not list, so no role can "
          + "be authorized to dispatch them: " + string.Join(", ", unknown));
    }
}
