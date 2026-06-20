using System.Security.Cryptography;
using System.Text;
using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Security;

/// <summary>
/// Authenticated field-level encryption for sensitive data at rest (AES-256-GCM).
///
/// New in the v1.8.0 migration. The Python build hardened DB file permissions but kept
/// payloads in plaintext; .NET gives us first-class AEAD, so sensitive columns (patch
/// bodies, decision notes) can be sealed with confidentiality + integrity. Output is a
/// self-describing token: <c>enc:v1:&lt;base64(nonce|tag|ciphertext)&gt;</c>, so a column can
/// hold a mix of legacy plaintext and encrypted values and still round-trip.
///
/// Key resolution order:
///   1. ANTHILL_ENCRYPTION_KEY env var (base64 or hex, 32 bytes)
///   2. a 0600 key file under the workspace (auto-generated once, owner-only)
/// </summary>
public sealed class FieldCipher
{
    private const string Prefix = "enc:v1:";
    private readonly byte[] _key;
    private readonly bool _enabled;

    public bool Enabled => _enabled;

    public FieldCipher(byte[]? key, bool enabled)
    {
        _key = key ?? new byte[32];
        _enabled = enabled && key is { Length: 32 };
    }

    /// <summary>Builds the cipher, materialising a workspace key file on first use if needed.</summary>
    public static FieldCipher CreateDefault()
    {
        try
        {
            var fromEnv = LoadKeyFromEnv();
            if (fromEnv is { Length: 32 }) return new FieldCipher(fromEnv, enabled: true);

            var keyPath = AnthillRuntime.PathFromScript($"{AnthillRuntime.DefaultWorkspace}/field.key");
            byte[] key;
            if (File.Exists(keyPath))
            {
                key = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
            }
            else
            {
                key = RandomNumberGenerator.GetBytes(32);
                Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
                File.WriteAllText(keyPath, Convert.ToBase64String(key));
                FileSecurity.HardenFilePermissions(keyPath);
            }
            return new FieldCipher(key.Length == 32 ? key : null, enabled: key.Length == 32);
        }
        catch
        {
            // If key management fails we degrade to pass-through rather than blocking the colony.
            return new FieldCipher(null, enabled: false);
        }
    }

    private static byte[]? LoadKeyFromEnv()
    {
        var raw = Environment.GetEnvironmentVariable("ANTHILL_ENCRYPTION_KEY");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { var b = Convert.FromBase64String(raw.Trim()); if (b.Length == 32) return b; } catch { /* try hex */ }
        try { var b = Convert.FromHexString(raw.Trim()); if (b.Length == 32) return b; } catch { /* give up */ }
        return null;
    }

    /// <summary>Seals a value. Returns plaintext unchanged when encryption is disabled or the value is null/empty.</summary>
    public string? Protect(string? plaintext)
    {
        if (!_enabled || string.IsNullOrEmpty(plaintext)) return plaintext;
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        var blob = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, blob, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, blob, nonce.Length + tag.Length, cipher.Length);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>Opens a sealed value. Passes through anything that is not an enc:v1 token (legacy plaintext).</summary>
    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix)) return stored;
        if (!_enabled) return stored; // cannot decrypt without a key; surface the token rather than throw
        try
        {
            var blob = Convert.FromBase64String(stored[Prefix.Length..]);
            var nonceLen = AesGcm.NonceByteSizes.MaxSize;
            var tagLen = AesGcm.TagByteSizes.MaxSize;
            var nonce = blob.AsSpan(0, nonceLen);
            var tag = blob.AsSpan(nonceLen, tagLen);
            var cipher = blob.AsSpan(nonceLen + tagLen);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, tagLen);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // Wrong key or tampered ciphertext: never silently return garbage.
            return "[ANTHILL: encrypted value could not be decrypted with the active key]";
        }
    }
}
