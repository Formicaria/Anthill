using Anthill.SDK.Security;

namespace Anthill.Modules.Homelab;

/// <summary>
/// The configuration this module was composed with. v3.8.7.
///
/// Static, and for the same reason <c>ReasoningProviders</c> is: the alternative was threading
/// <see cref="HomelabOptions"/> through the constructors of the repository, the scheduler, the
/// health runner, the notifier and the credential store — every one of which is constructed
/// directly by the 240 homelab tests. That change would have been a large behavioural edit wearing
/// a refactor's clothes, and the thing being held here is a per-PROCESS fact rather than a per-
/// colony one.
///
/// The defaults match the core's own defaults, so a homelab constructed without a composition root
/// — which is exactly what every test does — behaves as it always has.
/// </summary>
public static class HomelabRuntime
{
    private static HomelabOptions _options = new(
        DatabasePath: "anthill.db",
        StopFileName: "HOMELAB_STOP",
        HealthTimeoutMs: 5_000,
        NotificationsEnabled: false,
        SlackWebhook: null,
        DiscordWebhook: null,
        GenericWebhook: null,
        ColonyVersion: "0.0.0",
        WorkspaceRootPath: ".");

    private static IFieldCipher? _cipher;
    private static readonly object Gate = new();

    public static HomelabOptions Options
    {
        get { lock (Gate) return _options; }
    }

    /// <summary>
    /// Encrypts stored credentials. Null until a composition root supplies one — and null is a
    /// supported state, not an error: the colony runs unencrypted by default, and a homelab that
    /// refused to start without a cipher would be stricter than the core it lives in.
    /// </summary>
    public static IFieldCipher? Cipher
    {
        get { lock (Gate) return _cipher; }
    }

    /// <summary>Called once at startup, before anything homelab is constructed.</summary>
    public static void Configure(HomelabOptions options, IFieldCipher? cipher = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (Gate)
        {
            _options = options;
            _cipher = cipher;
        }
    }
}
