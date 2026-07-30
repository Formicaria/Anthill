using Anthill.Core.Configuration;
using Anthill.Core.Memory;

namespace Anthill.Core.Orchestration;

/// <summary>
/// v3.1.0 (ADR-001) — the composition root.
///
/// One place builds the object graph. Before this, the graph was built wherever someone needed a
/// piece of it: <c>ApiHost</c> owned a <c>Queen</c> as a public static, the CLI made its own, and
/// each read whatever the mutable runtime happened to say at the moment it ran. Nothing was wrong
/// with any single construction; the problem was that there was no single answer to "what is this
/// process running", and therefore no way to run two.
///
/// A host owns exactly one colony: one database, one resolved <see cref="RuntimeProfile"/>, one
/// <see cref="Queen"/>. Two hosts can exist in the same process with different configuration and
/// cannot see each other's — which is ADR-001's exit gate, and the property that made every
/// gate-touching test serialise itself around the globals.
///
/// Deliberately small. This is a composition root, not a container: no service locator, no
/// registration API, no lifetime scopes. The graph is six objects and it is written out longhand
/// so it can be read.
/// </summary>
public sealed class RuntimeHost : IDisposable
{
    /// <summary>The capability set every mission in this host is governed by.</summary>
    public RuntimeProfile Profile => Queen.Profile;

    /// <summary>The mission authority. Typed as the interface so callers see the contract rather
    /// than the implementation; <see cref="Queen"/> remains the only implementation.</summary>
    public IMissionCoordinator Coordinator => Queen;

    public Queen Queen { get; }

    public SqliteMemory Memory => Queen.Memory;

    private RuntimeHost(Queen queen) => Queen = queen;

    /// <summary>
    /// Build a host. <paramref name="options"/> null captures the live runtime — the behaviour the
    /// CLI and API host have always had. Passing options explicitly is what lets a second host
    /// exist alongside the first without either disturbing the other.
    /// </summary>
    public static RuntimeHost Create(SqliteMemory? memory = null, RuntimeOptions? options = null)
    {
        AnthillRuntime.Initialize();
        var captured = options ?? RuntimeOptions.Capture();
        // Tool grants are filled in by the Queen once its registry exists; resolving here would
        // report grants for tools nothing had built yet.
        var profile = RuntimeProfile.Resolve(captured, Array.Empty<string>());
        return new RuntimeHost(new Queen(memory, profile));
    }

    /// <summary>Configuration-health findings observed when this host resolved its profile. Surfaced
    /// at startup and at <c>/config/health</c>; degraded loudly, never silently.</summary>
    public IReadOnlyList<ConfigFinding> Findings => Profile.Findings;

    public void Dispose() => Queen.Dispose();
}
