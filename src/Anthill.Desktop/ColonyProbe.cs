namespace Anthill.Desktop;

/// <summary>
/// Is that port serving ANTHILL, or merely serving? The distinction decides boot-vs-attach, and a
/// bare TCP check cannot make it — any process on 8713 would read as "colony up" and the shell
/// would render someone else's service in a window titled Anthill.
/// </summary>
internal static class ColonyProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>The root serves the console itself; its markup names the product. An auth-gated
    /// deployment still serves the login shell, so this holds for locked-down installs too.</summary>
    public static bool IsAnthillServing(string baseUrl)
    {
        try
        {
            var body = Http.GetStringAsync(baseUrl + "/").GetAwaiter().GetResult();
            return body.Contains("ANTHILL", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool WaitUntilServing(string baseUrl, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (IsAnthillServing(baseUrl)) return true;
            Thread.Sleep(250);
        }
        return false;
    }
}
