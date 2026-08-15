namespace Anthill.Modules.Micromound;

/// <summary>
/// The configuration this module was composed with, held statically for the same reason
/// <c>HomelabRuntime</c> holds its own: what is being kept is a per-PROCESS fact, and threading it
/// through every constructor would be a behavioural edit wearing a refactor's clothes.
///
/// The defaults are deliberately the safe ones. A Micromound module constructed without a
/// composition root — which is what every test does — has no database, an unusable stop path, and
/// therefore cannot be mistaken for a configured colony.
/// </summary>
public static class MicromoundRuntime
{
    private static MicromoundOptions _options = new(
        DatabasePath: "anthill.db",
        StopFileName: "MICROMOUND_STOP",
        WorkspaceRootPath: ".",
        ColonyVersion: "0.0.0");

    private static IFieldCipher? _cipher;
    private static readonly object Gate = new();

    public static MicromoundOptions Options
    {
        get { lock (Gate) return _options; }
    }

    /// <summary>
    /// Encrypts stored enrollment tokens. Null runs them in plaintext, which is what the colony
    /// does by default — the same supported state the homelab credential store accepts.
    /// </summary>
    public static IFieldCipher? Cipher
    {
        get { lock (Gate) return _cipher; }
    }

    public static void Configure(MicromoundOptions options, IFieldCipher? cipher = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (Gate)
        {
            _options = options;
            _cipher = cipher;
        }
    }
}
