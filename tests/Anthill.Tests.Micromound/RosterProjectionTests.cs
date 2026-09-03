using Anthill.Modules.Micromound;
using Micromound.Protocol;
using Micromound.Runtime;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// THE PROJECTED ROSTER IS THE DEVICE'S ROSTER. v0.3.8.114.
///
/// `MicromoundRoster` names the seven default workers, and so does
/// `Micromound.Runtime.DefaultAnts`. Two stores of one fact — defect class 5b — and the copy exists
/// for a reason the brief states directly: the Anthill module may not reference the device runtime
/// (§33, "embed Micromound runtime inside Anthill"), and the wire contract it CAN reference does
/// not carry the roster.
///
/// So the duplication is made checkable rather than tolerated. This test project already references
/// `Micromound.Sim` — deliberately, so that envelopes come from the device implementation rather
/// than from whoever wrote the code under test — and Sim brings `Micromound.Runtime` with it. That
/// makes this a COMPILED comparison against the real declaration rather than a source scan of it,
/// which `docs/GUARDS.md` ranks two tiers higher: a renamed constant stops compiling here instead
/// of quietly matching nothing.
///
/// The production module still references only Protocol and Crypto. Only the tests see the runtime.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class RosterProjectionTests
{
    /// <summary>
    /// THE SEVEN NAMES AGREE, EXACTLY AND IN ORDER. Order matters because the colony view draws
    /// them in it — the coordinator first, then its six — and a projection that agreed as a set
    /// while disagreeing as a sequence would render a mound whose Mound Major was somewhere in the
    /// middle of its own workers.
    /// </summary>
    [Fact]
    public void TheProjectedNames_AreTheDeviceRuntimesOwn()
    {
        Assert.Equal(DefaultAnts.All, MicromoundRoster.Names);
    }

    /// <summary>
    /// And each constant matches, so a rename upstream cannot be absorbed by the list happening to
    /// stay the same length.
    /// </summary>
    [Fact]
    public void EachProjectedConstant_MatchesTheDeviceRuntimes()
    {
        Assert.Equal(DefaultAnts.MoundMajor, MicromoundRoster.MoundMajor);
        Assert.Equal(DefaultAnts.Scout, MicromoundRoster.Scout);
        Assert.Equal(DefaultAnts.Forager, MicromoundRoster.Forager);
        Assert.Equal(DefaultAnts.Guard, MicromoundRoster.Guard);
        Assert.Equal(DefaultAnts.Witness, MicromoundRoster.Witness);
        Assert.Equal(DefaultAnts.Cache, MicromoundRoster.Cache);
        Assert.Equal(DefaultAnts.Runner, MicromoundRoster.Runner);
    }

    /// <summary>Every name carries a role. A worker with no stated purpose renders as a blank row.</summary>
    [Fact]
    public void EveryStandardWorker_HasAStatedRole()
    {
        foreach (var name in MicromoundRoster.Names)
        {
            Assert.True(MicromoundRoster.Roles.ContainsKey(name), $"{name} has no role");
            Assert.False(string.IsNullOrWhiteSpace(MicromoundRoster.Roles[name]));
        }

        Assert.Equal(MicromoundRoster.Names.Count, MicromoundRoster.Roles.Count);
    }

    /// <summary>
    /// A MOUND WITH NO MANIFEST STILL HAS ALL SEVEN. They are the runtime, not a configuration —
    /// so an empty capability list says "not configured yet", where an absent worker would wrongly
    /// say "not present".
    /// </summary>
    [Fact]
    public void AnUnconfiguredMound_StillShowsTheStandardColony()
    {
        var workers = MicromoundRoster.For(null);

        Assert.Equal(7, workers.Count);
        Assert.All(workers, w => Assert.True(w.Standard));
        Assert.All(workers, w => Assert.Empty(w.Consumes));
    }

    /// <summary>
    /// A manifest ADDS optional workers on top of the seven; it never replaces them. ANTS.md: the
    /// standard colony is always the same, and specialization happens through capabilities.
    /// </summary>
    [Fact]
    public void AManifest_AddsItsOwnWorkersWithoutDisplacingTheStandardSeven()
    {
        var workers = MicromoundRoster.For(new MoundManifest
        {
            MoundId = "mm-greenhouse",
            Workers =
            [
                new WorkerDefinition
                {
                    Name = "Soil Ant",
                    Purpose = "soil moisture observation and trend",
                    RuntimeType = RuntimeTypes.Sensor,
                    Consumes = ["sense.soil_moisture"],
                    ActionCeiling = "observe",
                },
            ],
        });

        Assert.Equal(8, workers.Count);
        Assert.Equal(7, workers.Count(w => w.Standard));

        var soil = Assert.Single(workers, w => !w.Standard);
        Assert.Equal("Soil Ant", soil.Name);
        Assert.Equal(["sense.soil_moisture"], soil.Consumes);

        // And the standard seven are still first, in order.
        Assert.Equal(MicromoundRoster.Names, workers.Where(w => w.Standard).Select(w => w.Name).ToList());
    }

    /// <summary>
    /// A MANIFEST CANNOT REDEFINE A STANDARD WORKER BY NAMING ONE. ANTS.md forbids changing the
    /// fundamental role definitions, and a manifest that declares its own "Witness Ant" with a
    /// convenient ceiling is precisely that attempt. The standard definition wins and the duplicate
    /// is dropped rather than producing two Witnesses, which is the rendering that would let an
    /// operator believe the wrong one.
    /// </summary>
    [Fact]
    public void AManifestCannotRedefineAStandardWorker()
    {
        var workers = MicromoundRoster.For(new MoundManifest
        {
            MoundId = "mm-rover",
            Workers =
            [
                new WorkerDefinition
                {
                    Name = MicromoundRoster.Witness,
                    Purpose = "definitely still a witness",
                    RuntimeType = RuntimeTypes.Actuator,
                    ActionCeiling = "controlled",
                },
            ],
        });

        Assert.Equal(7, workers.Count);

        var witness = Assert.Single(workers, w => w.Name == MicromoundRoster.Witness);
        Assert.True(witness.Standard);
        Assert.Equal(MicromoundRoster.Roles[MicromoundRoster.Witness], witness.Role);
        Assert.Equal("observe", witness.ActionCeiling);
    }
}
