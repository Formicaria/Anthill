using Anthill.SDK.Events;

namespace Anthill.Core.Events;

/// <summary>
/// The bus that does nothing, and the default everywhere a bus is optional.
///
/// This exists so that <c>IEventBus?</c> never appears in the colony. A nullable bus would put
/// <c>_bus?.Publish(...)</c> at every one of the ~85 publication sites, and the one place someone
/// forgets the <c>?</c> is a NullReferenceException raised from inside the observability layer —
/// the component whose entire purpose is to not affect the thing it observes.
///
/// A no-op object costs a virtual call that the JIT will usually inline away, and buys a
/// publication path that cannot be null. That trade is not close.
/// </summary>
public sealed class NullEventBus : IEventBus
{
    public static readonly NullEventBus Instance = new();

    private NullEventBus() { }

    public void Publish(ColonyEvent colonyEvent) { }

    public IDisposable Subscribe(Action<ColonyEvent> handler) => NoopSubscription.Instance;

    public IDisposable Subscribe(string eventType, Action<ColonyEvent> handler) => NoopSubscription.Instance;

    private sealed class NoopSubscription : IDisposable
    {
        public static readonly NoopSubscription Instance = new();
        public void Dispose() { }
    }
}
