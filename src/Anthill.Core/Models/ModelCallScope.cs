namespace Anthill.Core.Models;

/// <summary>
/// Ambient, async-flow-local cancellation for model calls. A mission enters a scope carrying its
/// deadline/cancel token; every <see cref="IModelClient"/> links that token into each HTTP request
/// so an in-flight generation aborts the instant the mission times out or its job is cancelled —
/// without having to thread a <see cref="System.Threading.CancellationToken"/> through every ant
/// method signature.
///
/// The token is stored in an <see cref="System.Threading.AsyncLocal{T}"/>, so it flows across the
/// <c>Task.Run</c> continuations used by parallel task execution but stays isolated per mission.
/// Outside any scope the ambient token is <see cref="System.Threading.CancellationToken.None"/>,
/// so the CLI and unit tests behave exactly as they did before this mechanism existed.
/// </summary>
public static class ModelCallScope
{
    private static readonly AsyncLocal<CancellationToken> Ambient = new();

    /// <summary>The ambient token for the current mission, or <see cref="CancellationToken.None"/> outside a scope.</summary>
    public static CancellationToken Current => Ambient.Value;

    /// <summary>
    /// Enters a scope binding <paramref name="token"/> as the ambient model-call token. Disposing the
    /// returned handle restores the previously ambient token, so scopes nest safely.
    /// </summary>
    public static IDisposable Enter(CancellationToken token)
    {
        var previous = Ambient.Value;
        Ambient.Value = token;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly CancellationToken _previous;
        private bool _disposed;
        public Scope(CancellationToken previous) => _previous = previous;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
