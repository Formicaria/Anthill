using Micromound.Crypto;
using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>
/// THE COLONY'S SIGNING IDENTITY — the half of the link `.60` deliberately did not build.
/// v0.3.8.114.
///
/// M1 shipped the uplink: a mound could enroll, beat, and be refused, and the colony could see it.
/// It could not DIRECT it, and the module said so plainly — "M1 has no command path, so the colony
/// can see mounds and cannot direct them." Everything that makes a controller a controller in
/// `UPSTREAM.md`'s sense — charters, configuration, missions, stop as a signed order rather than a
/// local refusal — needs a key to sign with. This is that key.
///
/// WHAT THE PROTOCOL REQUIRES OF IT, and none of it is negotiable on our side:
///
/// - Ed25519, over an envelope's CANONICAL BYTES (PROTOCOL.md §2). Those bytes carry `sig` as an
///   empty string rather than omitting the field, so signing never disturbs the hash chain and the
///   chain never covers the signature. Both are checked, separately. We do not compute them — the
///   protocol library does, and that is the point of consuming the contract rather than copying it.
/// - The key id is `controller` (<see cref="KeyIds.Controller"/>). A mound looks the colony's public
///   key up under that name, having received it at enrollment.
/// - There is no unsigned mode. A downlink envelope this colony cannot sign is a downlink envelope
///   this colony does not send.
///
/// WHERE THE SEED LIVES, and why not in configuration. `UPSTREAM.md` says to use the existing
/// credential storage rather than putting private key material in config JSON or a UI payload, and
/// `SAFETY.md` prohibits any endpoint or envelope that reads a private key back. So the seed is
/// held in the mound store, encrypted by the colony's field cipher when one is configured, and
/// nothing on this type returns it: <see cref="PublicKeyHex"/> exists, a matching private accessor
/// does not, and that is a deliberate absence rather than an oversight to fix later.
///
/// It is generated ONCE, on first use, and never rotated automatically. Rotating a controller key
/// silently would orphan every enrolled mound — each holds the old public key and would refuse
/// every subsequent charter as `unknown_key`, correctly, while looking to an operator like the
/// fleet had simply stopped obeying. Rotation is therefore an explicit operator act that must
/// re-enroll the fleet, and it is not implemented here rather than being implemented badly.
/// </summary>
public sealed class MicromoundIdentity
{
    private readonly IMoundStore _store;
    private readonly object _gate = new();
    private Ed25519KeyPair? _keyPair;

    public MicromoundIdentity(IMoundStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// The colony's public key, lowercase hex — what a mound is handed at enrollment and verifies
    /// every downlink envelope against thereafter.
    /// </summary>
    public string PublicKeyHex => Convert.ToHexStringLower(KeyPair().PublicKey);

    /// <summary>
    /// A signer for downlink envelopes. Fresh each call rather than cached: the signer is a thin
    /// binding of a key id to a key pair, and holding one for the lifetime of the process is how a
    /// rotated key ends up still signing with the old one.
    /// </summary>
    public IEnvelopeSigner Signer() => new Ed25519EnvelopeSigner(KeyIds.Controller, KeyPair());

    /// <summary>
    /// Sign an envelope in place and return it. The single place downlink is signed — a second one
    /// would be a second answer to "what does this colony's authority look like on the wire".
    /// </summary>
    public Envelope Sign(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return EnvelopeSigning.Sign(envelope, Signer());
    }

    /// <summary>
    /// Verifier for UPLINK, resolving a mound's device key from the registry by `mound_id`.
    ///
    /// Built per call over a live view of the store, deliberately. A cached directory is a
    /// directory that still holds a key an operator has just decommissioned, and PROTOCOL.md §2 is
    /// explicit that a key the verifier does not hold is a refusal rather than a prompt to learn
    /// one — so what "does not hold" means has to be current.
    /// </summary>
    public IEnvelopeVerifier UplinkVerifier() => new Ed25519EnvelopeVerifier(new RegistryKeys(_store));

    private Ed25519KeyPair KeyPair()
    {
        lock (_gate)
        {
            if (_keyPair is not null) return _keyPair;

            var stored = _store.GetControllerSeed();
            if (stored is { Length: Ed25519KeyPair.SeedLength })
            {
                _keyPair = Ed25519KeyPair.FromSeed(stored);
                return _keyPair;
            }

            // First use. A colony that has never signed anything mints its identity here rather
            // than at install time, so a deployment that never attaches a mound never holds a
            // signing key it did not need.
            _keyPair = Ed25519KeyPair.Generate();
            _store.PutControllerSeed(_keyPair.Seed);
            return _keyPair;
        }
    }

    /// <summary>
    /// The mound registry, read as a public-key directory. `keyId` is the `mound_id`, per
    /// PROTOCOL.md §2 — uplink is verified against the sending mound's own device key.
    /// </summary>
    private sealed class RegistryKeys(IMoundStore store) : IPublicKeyDirectory
    {
        public bool TryGetPublicKey(string keyId, out byte[] publicKey)
        {
            publicKey = [];

            var mound = store.GetMound(keyId);

            // An un-enrolled mound has an empty key, and that is NOT a key of length zero to be
            // compared against — it is the absence enrollment exists to fill. Returning false here
            // is what makes "no trust on first use" true: a device that has not been granted an
            // identity cannot assert one by signing with it.
            if (mound is null || string.IsNullOrEmpty(mound.PublicKey)) return false;

            if (!MicromoundEnrollment.TryDecodeKey(mound.PublicKey, out var decoded)) return false;

            publicKey = decoded;
            return true;
        }
    }
}
