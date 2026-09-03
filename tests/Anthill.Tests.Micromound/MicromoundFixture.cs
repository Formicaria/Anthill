using Anthill.Modules.Micromound;
using Anthill.SDK.Events;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// <see cref="MicromoundRuntime"/> holds per-process configuration in statics, the same shape
/// <c>HomelabRuntime</c> uses. That is fine in a colony and hostile to parallel tests, so every
/// class here shares one non-parallel collection rather than racing each other's workspace paths.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MicromoundCollection
{
    public const string Name = "micromound";
}

/// <summary>
/// Captures what the module told the colony. Refusals are supposed to be loud and audited —
/// a test that only checks the return value would pass on a module that silently swallowed
/// everything.
/// </summary>
public sealed class RecordingEventBus : IEventBus
{
    private readonly List<ColonyEvent> _events = [];
    private readonly object _gate = new();

    public IReadOnlyList<ColonyEvent> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    public void Publish(ColonyEvent colonyEvent)
    {
        lock (_gate) _events.Add(colonyEvent);
    }

    public IDisposable Subscribe(Action<ColonyEvent> handler) => new Noop();

    public IDisposable Subscribe(string eventType, Action<ColonyEvent> handler) => new Noop();

    public bool Saw(string eventType) =>
        Events.Any(e => string.Equals(e.EventType, eventType, StringComparison.Ordinal));

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// THE WHOLE CONTROLLER, COMPOSED THE ONE WAY. v0.3.8.114.
///
/// The sync beat now needs the identity (it signs acks and stop orders), the charter service (an
/// acknowledged beat renews the lease) and the evidence service (it ingests what arrived), which is
/// four constructor arguments a test would otherwise assemble by hand each time — and assemble
/// slightly differently, which is how a test ends up proving something the composition root does
/// not do. `ApiHost.InitMicromound` builds exactly this graph.
/// </summary>
public sealed record Colony(
    InMemoryMoundStore Store,
    RecordingEventBus Bus,
    MicromoundIdentity Identity,
    MicromoundCharters Charters,
    MicromoundConfiguration Configuration,
    MicromoundMissions Missions,
    MicromoundEvidence Evidence,
    MicromoundResolver Resolver,
    MicromoundSync Sync)
{
    public static Colony Build(InMemoryMoundStore? store = null, RecordingEventBus? bus = null)
    {
        store ??= new InMemoryMoundStore();
        bus ??= new RecordingEventBus();

        var identity = new MicromoundIdentity(store);
        var charters = new MicromoundCharters(store, identity, bus);
        var evidence = new MicromoundEvidence(store, bus);

        return new Colony(store, bus, identity, charters,
            new MicromoundConfiguration(store, identity, bus),
            new MicromoundMissions(store, identity, bus),
            evidence,
            new MicromoundResolver(store),
            new MicromoundSync(store, bus, identity, charters, evidence));
    }
}

/// <summary>A throwaway workspace so the MICROMOUND_STOP file can be created and removed safely.</summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string stopFileName = "MICROMOUND_STOP")
    {
        Root = Path.Combine(Path.GetTempPath(), "micromound-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, MicromoundStop.DirectoryName));
        StopFileName = stopFileName;

        MicromoundRuntime.Configure(new MicromoundOptions(
            DatabasePath: Path.Combine(Root, "anthill.db"),
            StopFileName: stopFileName,
            WorkspaceRootPath: Root,
            ColonyVersion: "test",
            EnrollmentTokenTtlMinutes: 30,
            MoundOfflineAfterMissedBeats: 3));
    }

    public string Root { get; }

    public string StopFileName { get; }

    public MicromoundOptions Options => MicromoundRuntime.Options;

    public void EngageGlobalStop() =>
        File.WriteAllText(Path.Combine(Root, MicromoundStop.DirectoryName, StopFileName), "stop");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not a test failure.
        }
    }
}
