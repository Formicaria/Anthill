using Anthill.SDK.Modules;

namespace Anthill.Modules.Micromound;

/// <summary>
/// MICROMOUND as a module — M1, read-only.
///
/// The colony can see mounds and cannot direct them. That is not a temporary shortcut on the way
/// to something better; it is the first design rule (observe before act) applied to a link whose
/// other end moves physical hardware. Read-only lands, then actions arrive behind the approval
/// pipeline, and a phase does not start while the previous one's tests are red.
///
/// Concretely, what this module does NOT contain is the point of it: no charter issuance, no
/// mission assignment, no actuation, no ceiling that anything here can raise. The only downlink
/// it can produce at all is a stop order, and a stop is a command to stop acting.
///
/// The rules from Anthill.Modules/README.md hold as written: SDK only, no core reference, no
/// reference to another module, no I/O in Register.
/// </summary>
public sealed class MicromoundModule : IAnthillModule
{
    public const string ModuleName = "micromound";

    private readonly MicromoundOptions _options;
    private readonly IFieldCipher? _cipher;

    /// <param name="options">Built by the composition root from the live runtime.</param>
    /// <param name="cipher">Encrypts stored enrollment tokens. Null runs them unencrypted, which
    /// is what the colony does by default.</param>
    public MicromoundModule(MicromoundOptions options, IFieldCipher? cipher = null)
    {
        _options = options;
        _cipher = cipher;
    }

    public string Name => ModuleName;

    public string Version => "0.1.0";

    /// <summary>
    /// Configuration only, per the <see cref="IAnthillModule"/> contract — and the rule matters
    /// here for a reason the homelab does not have. A mound is a device that may be asleep, on a
    /// dead battery, or physically absent. A colony that dialled one during registration would be
    /// a colony that refuses to boot because a Pi in a shed is off, which is the worst possible
    /// coupling to hand a system whose entire premise is that disconnection is normal.
    ///
    /// Nothing here opens a socket. Mounds dial in; the colony never reaches out.
    /// </summary>
    public void Register(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MicromoundRuntime.Configure(_options, _cipher);

        context.Events.Publish(new ColonyEvent
        {
            EventType = EventTypes.ModuleRegistered,
            Message = "Micromound available: mound registry, enrollment, telemetry sync. Read-only — " +
                      "no command path until M2.",
            Metadata = new Dictionary<string, object?>
            {
                ["module"] = Name,
                ["version"] = Version,
                ["phase"] = "M1",
                ["command_path"] = false,
                ["protocol_version"] = global::Micromound.Protocol.ProtocolVersion.Current,
                ["encrypted_tokens"] = _cipher?.Enabled ?? false,
                ["stop_file"] = MicromoundStop.PathFor(_options),
                ["widget_kinds"] = string.Join(",", MicromoundWidgetKinds.All),
                ["permissions"] = string.Join(",",
                    MicromoundPermissions.Read, MicromoundPermissions.Manage, MicromoundPermissions.Approve),
            },
        });
    }
}
