namespace Anthill.SDK.Security;

/// <summary>
/// The outbound-target blocklist <see cref="Anthill.SDK.Common.UrlSafety"/> consults. v3.8.12.
///
/// Live-reading properties, not a snapshot, for the reason <see cref="Anthill.SDK.Tools.IToolRuntimeOptions"/>
/// records at length: the fields behind these are mutable statics that an operator or a test can add
/// to at runtime. A captured value would make a test that blocks a host pass while the production
/// path kept letting it through — green for the wrong reason.
///
/// Two members, two shapes, and the shapes are not interchangeable. The hostname list is matched by
/// equality and is a set; the suffix list is matched by <c>EndsWith</c> and is ordered, so it is a
/// list. The core declares them that way (<c>HashSet&lt;string&gt;</c> and <c>string[]</c>), and
/// flattening them to one type here would misrepresent how they are used.
/// </summary>
public interface ISsrfPolicy
{
    /// <summary>Hostnames refused outright, matched case-insensitively by equality.</summary>
    IReadOnlySet<string> BlockedHostnames { get; }

    /// <summary>Suffixes refused, matched case-insensitively with <c>EndsWith</c>.</summary>
    IReadOnlyList<string> BlockedHostSuffixes { get; }
}
