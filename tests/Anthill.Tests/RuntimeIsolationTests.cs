using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.1.0 — ADR-001's exit gate: "Two runtime instances can execute tests in the same process
/// without configuration leakage."
///
/// This is the test the phase exists to make writable. Before the refactor it could not be
/// expressed at all: a Queen read <c>EnableModelRouting</c>, <c>UseOllama</c>,
/// <c>EnableFileTools</c> and <c>EnableFileWriting</c> out of mutable statics during
/// construction, so "two Queens with different configuration" was not a thing that could exist —
/// the second one to be built simply overwrote what the first had been told. That is why the
/// suite grew <c>[Collection("Autonomy")]</c> and <c>[Collection("specialist-gates")]</c>
/// attributes, and why an assembly-wide parallelisation ban was eventually needed on top of them.
///
/// Note what is NOT claimed here. These tests share a process with the rest of the suite, which
/// still mutates globals, so they build their hosts from EXPLICIT options rather than from the
/// ambient runtime. That is the point: a host composed from explicit options is immune to what
/// the statics are doing, and that immunity is exactly what the exit gate asks for.
/// </summary>
public class RuntimeIsolationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_isolation_" + Guid.NewGuid().ToString("N"));

    public RuntimeIsolationTests()
    {
        AnthillRuntime.Initialize();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Db(string name) => new(Path.Combine(_dir, name + ".db"));

    private static RuntimeOptions With(RuntimeOptions basis, bool fileTools, bool fileWriting, bool modelRouting) =>
        basis with { FileTools = fileTools, FileWriting = fileWriting, ModelRouting = modelRouting };

    /// <summary>
    /// THE gate. Two hosts, different capability configuration, same process, alive at the same
    /// time — each keeping its own answer.
    /// </summary>
    [Fact]
    public void TwoHosts_WithDifferentCapabilities_CoexistWithoutLeakage()
    {
        var basis = RuntimeOptions.Capture();

        using var restricted = RuntimeHost.Create(Db("restricted"),
            With(basis, fileTools: false, fileWriting: false, modelRouting: false));
        using var permissive = RuntimeHost.Create(Db("permissive"),
            With(basis, fileTools: true, fileWriting: true, modelRouting: false));

        // Configuration
        Assert.False(restricted.Profile.Options.FileTools);
        Assert.True(permissive.Profile.Options.FileTools);
        Assert.False(restricted.Profile.Options.FileWriting);
        Assert.True(permissive.Profile.Options.FileWriting);

        // ...and the configuration actually shaped what got BUILT, which is the part that used to
        // be impossible. A restricted host has no file tools registered at all.
        Assert.False(restricted.Profile.HasTool("read_text_file"));
        Assert.True(permissive.Profile.HasTool("read_text_file"));
        Assert.False(restricted.Profile.HasTool("write_text_file"));
        Assert.True(permissive.Profile.HasTool("write_text_file"));

        // Neither host's write permissions bled into the other's.
        Assert.True(restricted.Profile.Writes.IsReadOnly);
        Assert.False(permissive.Profile.Writes.IsReadOnly);
    }

    /// <summary>
    /// Order independence. If construction still read globals, building the permissive host second
    /// would leave the restricted one describing capabilities it does not have — the class of bug
    /// the [Collection] attributes were papering over.
    /// </summary>
    [Fact]
    public void TheOrderHostsAreBuiltIn_DoesNotChangeWhatEitherOneIs()
    {
        var basis = RuntimeOptions.Capture();
        var restrictedOptions = With(basis, fileTools: false, fileWriting: false, modelRouting: false);
        var permissiveOptions = With(basis, fileTools: true, fileWriting: true, modelRouting: false);

        using (var a = RuntimeHost.Create(Db("a1"), restrictedOptions))
        using (var b = RuntimeHost.Create(Db("b1"), permissiveOptions))
        {
            Assert.False(a.Profile.HasTool("write_text_file"));
            Assert.True(b.Profile.HasTool("write_text_file"));
        }

        // Same two hosts, built in the opposite order.
        using (var b = RuntimeHost.Create(Db("b2"), permissiveOptions))
        using (var a = RuntimeHost.Create(Db("a2"), restrictedOptions))
        {
            Assert.False(a.Profile.HasTool("write_text_file"));
            Assert.True(b.Profile.HasTool("write_text_file"));
        }
    }

    /// <summary>
    /// A host is immune to the ambient runtime after construction. Flipping the global that a
    /// capability came from does not change what an already-composed host is.
    /// </summary>
    [Fact]
    public void MutatingTheGlobalAfterConstruction_DoesNotReachAnExistingHost()
    {
        var basis = RuntimeOptions.Capture();
        using var host = RuntimeHost.Create(Db("immune"), With(basis, fileTools: true, fileWriting: false, modelRouting: false));

        Assert.False(host.Profile.Options.FileWriting);
        Assert.False(host.Profile.HasTool("write_text_file"));

        var prior = AnthillRuntime.EnableFileWriting;
        try
        {
            AnthillRuntime.EnableFileWriting = true;      // the global moves
            Assert.False(host.Profile.Options.FileWriting);       // the host does not
            Assert.False(host.Profile.HasTool("write_text_file"));
            Assert.True(host.Profile.Writes.IsReadOnly);
        }
        finally { AnthillRuntime.EnableFileWriting = prior; }
    }

    /// <summary>
    /// Each host owns its own colony: separate database, separate mission history. Two hosts must
    /// not be able to see or corrupt each other's state.
    /// </summary>
    [Fact]
    public void EachHostOwnsItsOwnColony()
    {
        var basis = RuntimeOptions.Capture();
        using var first = RuntimeHost.Create(Db("colony-one"), basis);
        using var second = RuntimeHost.Create(Db("colony-two"), basis);

        Assert.NotSame(first.Queen, second.Queen);
        Assert.NotSame(first.Memory, second.Memory);
        Assert.NotEqual(first.Memory.DbPath, second.Memory.DbPath);

        var mission = new Anthill.Core.Domain.Mission { Goal = "only in the first colony" };
        first.Memory.SaveMission(mission);

        Assert.Contains(first.Memory.GetRecentMissions(10), m => m["id"]?.ToString() == mission.Id);
        Assert.DoesNotContain(second.Memory.GetRecentMissions(10), m => m["id"]?.ToString() == mission.Id);
    }

    /// <summary>
    /// The coordinator contract: one mission authority per host, and it is the Queen. ADR-001's
    /// explicit prohibition was against decomposition producing a second lifecycle owner.
    /// </summary>
    [Fact]
    public void AHostExposesExactlyOneMissionAuthority()
    {
        using var host = RuntimeHost.Create(Db("authority"), RuntimeOptions.Capture());

        Assert.IsType<Queen>(host.Coordinator);
        Assert.Same(host.Queen, host.Coordinator);
        Assert.Same(host.Profile, host.Coordinator.Profile);
    }

    /// <summary>
    /// A mission's context is resolved against the host's profile, not against the ambient runtime
    /// — so a mission cannot be governed by configuration its colony never saw.
    /// </summary>
    [Fact]
    public void AMissionsPlanIsGovernedByItsOwnHostsProfile()
    {
        var basis = RuntimeOptions.Capture();
        using var readOnlyHost = RuntimeHost.Create(Db("plan-readonly"),
            With(basis, fileTools: true, fileWriting: false, modelRouting: false));

        var plan = readOnlyHost.Coordinator.PlanPreview("verify the parser only; no patches and do not modify files");

        Assert.True(plan.Constraints.BlocksPatches);
        Assert.DoesNotContain(plan.Tasks, t => t.AssignedAnt == "coder" && t.Status != TaskStatus.Failed);
    }
}
