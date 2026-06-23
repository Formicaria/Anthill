using System.Security.Cryptography;

namespace Anthill.Core.Security;

/// <summary>
/// Salted PBKDF2-SHA256 password hashing for operator accounts. We never store a plaintext or
/// reversible password — only a self-describing hash string of the form:
///
///     pbkdf2_sha256$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;
///
/// Storing the algorithm and iteration count inline means the work factor can be raised later
/// without invalidating existing hashes. Verification is constant-time so a wrong password can't
/// be distinguished from a near-miss by timing.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 120_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "pbkdf2_sha256";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Minimal password policy. Kept simple and human-friendly: at least 8 characters. The caller
    /// surfaces the returned reason to the operator; an empty string means the password is fine.
    /// </summary>
    public static string Validate(string? password)
    {
        password ??= "";
        if (password.Length < 8) return "Password must be at least 8 characters.";
        if (password.Trim() != password) return "Password must not start or end with whitespace.";
        return "";
    }
}
