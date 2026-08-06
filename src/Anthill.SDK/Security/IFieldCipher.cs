namespace Anthill.SDK.Security;

/// <summary>
/// Encrypts a single stored field. v3.8.7.
///
/// The colony's AES-GCM implementation stays in <c>Anthill.Core.Security.FieldCipher</c>, because
/// it resolves its key from a file path derived from the runtime's workspace configuration — real
/// I/O against real configuration, which is core work. What a module needs is narrower: turn a
/// secret into stored text and back.
///
/// So the homelab's credential store takes this instead of the class. That inverts the last piece
/// of the homelab's dependency on the core: a module that constructs a cipher would need the key
/// resolution, and the key resolution needs the runtime.
///
/// <c>Protect</c> and <c>Unprotect</c> are null-tolerant in both directions, which is not
/// squeamishness — a credential row can legitimately hold no secret yet, and the alternative is a
/// null check at every one of the store's call sites.
/// </summary>
public interface IFieldCipher
{
    /// <summary>False when encryption is not configured. Callers must not treat that as a failure:
    /// the colony runs unencrypted by default and the stored value is then plaintext.</summary>
    bool Enabled { get; }

    string? Protect(string? plaintext);

    string? Unprotect(string? stored);
}
