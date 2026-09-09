namespace Anthill.Core.Configuration;

/// <summary>
/// THE COLONY COULD NOT CREATE ONE OF ITS OWN STORAGE DIRECTORIES, AND THIS SAYS WHICH SETTING
/// ASKED FOR IT. v0.3.8.152.
///
/// The failure this replaces was a bare <see cref="UnauthorizedAccessException"/> from
/// <c>Directory.CreateDirectory</c>, surfaced to a desktop operator as:
///
///     Access to the path 'C:\Program Files\Anthill\workspace' is denied.
///
/// Every word of that is true and none of it is actionable. Six configuration keys resolve to a
/// directory during boot; the message named the path that lost and left the operator to work
/// backwards from a filesystem location to the setting that produced it — which, for a path built
/// by joining a relative value onto a home that is itself derived, is not reliably possible. The
/// house rule is that a refusal names the layer that said no. Naming the OS is not enough when the
/// operator's only lever is a key in a JSON file.
///
/// So the message carries four facts, in the order they are useful: the key, the value as written,
/// where that value resolved to, and the file to edit. The underlying exception is kept as the
/// inner exception rather than being flattened into the text, because the reason the write was
/// refused — permission, a missing volume, a name that is illegal on this filesystem — is the OS's
/// to explain and it explains it well.
/// </summary>
public sealed class ColonyStorageException : Exception
{
    public string Key { get; }
    public string AuthoredValue { get; }
    public string ResolvedPath { get; }

    public ColonyStorageException(
        string key, string authoredValue, string resolvedPath, string configPath, Exception inner)
        : base(Describe(key, authoredValue, resolvedPath, configPath, inner), inner)
    {
        Key = key;
        AuthoredValue = authoredValue;
        ResolvedPath = resolvedPath;
    }

    private static string Describe(
        string key, string authored, string resolved, string configPath, Exception inner)
    {
        // The resolved path is only worth printing when it differs from what the operator typed;
        // repeating an absolute value back at them adds a line and no information.
        var where = string.Equals(authored, resolved, StringComparison.OrdinalIgnoreCase)
            ? $"'{authored}'"
            : $"'{authored}', which resolves to '{resolved}'";

        var file = string.IsNullOrWhiteSpace(configPath) ? "the ANTHILL config file" : configPath;

        return $"ANTHILL cannot start: the setting {key} is {where}, and this colony cannot create "
             + $"or write that directory. {inner.Message} "
             + $"Edit {key} in {file} to a directory this user owns, or set ANTHILL_HOME to move the "
             + "whole colony somewhere writable. This is storage the colony cannot run without, so "
             + "it refuses to start rather than begin a second, empty history somewhere else.";
    }
}
