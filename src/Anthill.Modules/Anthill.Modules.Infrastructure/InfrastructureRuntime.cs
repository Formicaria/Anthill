using Anthill.SDK.Security;

namespace Anthill.Modules.Infrastructure;

/// <summary>
/// The configuration this module was composed with. v3.8.7.
///
/// Static, and for the same reason <c>ReasoningProviders</c> is: the alternative was threading
/// <see cref="InfrastructureOptions"/> through the constructors of the repository, the scheduler, the
/// health runner, the notifier and the credential store — every one of which is constructed
/// directly by the 240 infrastructure tests. That change would have been a large behavioural edit wearing
/// a refactor's clothes, and the thing being held here is a per-PROCESS fact rather than a per-
/// colony one.
///
/// The defaults match the core's own defaults, so a infrastructure constructed without a composition root
/// — which is exactly what every test does — behaves as it always has.
/// </summary>
public static class InfrastructureRuntime
{
    private static InfrastructureOptions _options = new(
        DatabasePath: "anthill.db",
        StopFileName: "INFRASTRUCTURE_STOP",
        HealthTimeoutMs: 5_000,
        NotificationsEnabled: false,
        SlackWebhook: null,
        DiscordWebhook: null,
        GenericWebhook: null,
        ColonyVersion: "0.0.0",
        WorkspaceRootPath: ".");

    private static IFieldCipher? _cipher;
    private static readonly object Gate = new();

    public static InfrastructureOptions Options
    {
        get { lock (Gate) return _options; }
    }

    /// <summary>
    /// Encrypts stored credentials. Null until a composition root supplies one — and null is a
    /// supported state, not an error: the colony runs unencrypted by default, and a infrastructure that
    /// refused to start without a cipher would be stricter than the core it lives in.
    /// </summary>
    public static IFieldCipher? Cipher
    {
        get { lock (Gate) return _cipher; }
    }

    /// <summary>Called once at startup, before anything infrastructure is constructed.</summary>
    public static void Configure(InfrastructureOptions options, IFieldCipher? cipher = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (Gate)
        {
            _options = options;
            _cipher = cipher;
        }
    }
}
