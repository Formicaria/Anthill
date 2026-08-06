using Anthill.SDK.Modules;

namespace Anthill.Modules.Homelab;

/// <summary>
/// The homelab capability, as a module. v3.8.7.
///
/// 6,549 lines of infrastructure knowledge — inventory, health, incidents, risk, approvals,
/// notifications, and nine integrations — that the colony is entirely able to run without. Nothing
/// in <c>Anthill.Core</c> names a type in this assembly, and the core's own tests never load it.
///
/// The move was possible because the seam was measured rather than estimated. The apparent coupling
/// was twenty <c>Anthill.Core.Common</c> imports; the real coupling was two pure helpers, one pure
/// action vocabulary shared with shadow mode, eleven settings, and one cipher.
/// </summary>
public sealed class HomelabModule : IAnthillModule
{
    private readonly HomelabOptions _options;
    private readonly SDK.Security.IFieldCipher? _cipher;

    /// <param name="options">Built by the composition root from the live runtime — see
    /// <see cref="HomelabOptions"/>.</param>
    /// <param name="cipher">Encrypts stored credentials. Null runs the homelab unencrypted, which
    /// is what the colony does by default.</param>
    public HomelabModule(HomelabOptions options, SDK.Security.IFieldCipher? cipher = null)
    {
        _options = options;
        _cipher = cipher;
    }

    public string Name => "homelab";

    public string Version => "3.8.7";

    /// <summary>
    /// No I/O, per the <see cref="IAnthillModule"/> contract — and here that rule earns its keep
    /// more than anywhere else. Registration must not touch a Proxmox node, run a health check or
    /// open a credential: every one of those reaches hardware that may be asleep, and a colony that
    /// would not boot until the homelab answered would be the worst possible coupling to introduce
    /// at the exact moment the homelab stopped being part of the core.
    ///
    /// Configuration only. The API's <c>InitHomelab</c> still constructs the repository, scheduler
    /// and runners after this, and the scheduler is what eventually talks to anything.
    /// </summary>
    public void Register(IModuleContext context)
    {
        HomelabRuntime.Configure(_options, _cipher);

        context.Events.Publish(new SDK.Events.ColonyEvent
        {
            EventType = SDK.Events.EventTypes.ModuleRegistered,
            Message = "Homelab available: inventory, health, incidents, risk, approvals, integrations.",
            Metadata = new Dictionary<string, object?>
            {
                ["module"] = Name,
                ["version"] = Version,
                ["notifications_enabled"] = _options.NotificationsEnabled,
                ["encrypted_credentials"] = _cipher?.Enabled ?? false,
            },
        });
    }
}
