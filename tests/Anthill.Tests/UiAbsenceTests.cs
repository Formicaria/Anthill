using Anthill.Api;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The FALLBACK behaviour of <c>ApiHost.LoadUiAsset</c>. Not the no-UI gate.
///
/// v3.8.17 claimed this file was "phase 6's exit gate, made executable", and used it to mark the
/// "core runs without UI" criterion met. That was wrong, and an external review said so: every asset
/// is an <c>EmbeddedResource</c>, so they are required at BUILD time and cannot be absent from a
/// binary that exists. Asking for a fabricated resource name exercises <c>FirstOrDefault</c>
/// returning null. It proves a null check.
///
/// The real gate is the <c>no-ui-boot</c> CI job added in v3.8.18: it publishes with
/// <c>-p:AnthillNoUi=true</c>, asserts the assets are genuinely not in the binary, boots it, and
/// requires <c>/health</c> to answer. That needs a build and a running process, so it lives in CI
/// rather than here.
///
/// What THIS file is still worth: the fallback path is what makes that boot survivable rather than a
/// crash, and it is cheap to pin. Kept for that, and only that.
/// </summary>
public class UiAbsenceTests
{
    [Fact]
    public void AMissingAsset_DegradesToItsFallback_RatherThanThrowing()
    {
        var missing = ApiHost.LoadUiAsset("no-such-asset-" + Guid.NewGuid().ToString("N") + ".js", "fallback");

        Assert.Equal("fallback", missing);
    }

    /// <summary>
    /// The default fallback is empty, not null. A null would reach the response pipeline and fail
    /// somewhere far from the cause.
    /// </summary>
    [Fact]
    public void AMissingAssetWithNoFallback_IsEmptyRatherThanNull()
    {
        var missing = ApiHost.LoadUiAsset("no-such-asset-" + Guid.NewGuid().ToString("N") + ".css");

        Assert.NotNull(missing);
        Assert.Equal("", missing);
    }

    /// <summary>
    /// And the assets that DO ship are found after phase 6 moved them out of the API project.
    ///
    /// This one is load-bearing in the ordinary build. `LoadUiAsset` matches by resource-name SUFFIX,
    /// so a move that changed the prefix would still have worked and one that changed the suffix
    /// would have served an empty console with no build error and no failing test. The csproj pins
    /// each `LogicalName`; this asserts the pinning holds.
    /// </summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("app.js")]
    [InlineData("mission-thread.js")]
    [InlineData("dashboard-grid.js")]
    [InlineData("dashboard-grid.css")]
    [InlineData("colony-topology.js")]
    [InlineData("colony-live.js")]
    [InlineData("colony-renderer.js")]
    [InlineData("colony-host.js")]
    [InlineData("colony-hud.js")]
    [InlineData("vendor.three.min.js")]
    public void EveryShippedAsset_IsEmbeddedAndFound(string asset)
    {
        var content = ApiHost.LoadUiAsset(asset);

        Assert.False(string.IsNullOrWhiteSpace(content),
            $"The embedded UI asset '{asset}' was not found. Phase 6 moved these to src/Anthill.UI/ "
          + "and pins each LogicalName in Anthill.Api.csproj — if that pinning is broken, the "
          + "console serves blank with no other symptom. (This assertion assumes a normal build; "
          + "the AnthillNoUi build deliberately has none of them, and is checked in CI instead.)");
    }

    /// <summary>
    /// THE VENDORED three.js IS THE PINNED BUILD, AND IT IS REALLY three.js. v0.3.8.115.
    ///
    /// `LoadUiAsset` matches by resource-name SUFFIX and returns "" for a miss, so a broken pin, a
    /// truncated file or an empty placeholder all produce the same symptom: a console that loads,
    /// reports no error, and renders nothing. `EveryShippedAsset_IsEmbeddedAndFound` above catches
    /// only the empty case.
    ///
    /// So this reads the bytes. The version banner pins WHICH build, the UMD preamble pins that it
    /// is the non-module build the `script-src 'self'` load depends on, and the size floor pins that
    /// it is the whole thing rather than a first chunk.
    /// </summary>
    [Fact]
    public void TheVendoredThreeJs_IsThePinnedUmdBuild()
    {
        var three = ApiHost.LoadUiAsset("vendor.three.min.js");

        Assert.Contains("three.js", three, StringComparison.OrdinalIgnoreCase);

        // The UMD preamble. An ES-module build would not define `window.THREE` under a plain
        // `<script src>`, and the console has no import map and no bundler to fix that.
        Assert.Contains("typeof exports", three, StringComparison.Ordinal);
        Assert.Contains("REVISION", three, StringComparison.Ordinal);

        Assert.True(three.Length > 400_000,
            $"the vendored three.js is {three.Length} bytes, which is far short of the real minified "
          + "build. A truncated or placeholder file loads without error and renders nothing.");
    }

    /// <summary>
    /// THE CONSOLE NEVER REACHES THE INTERNET FOR CODE. v0.3.8.115.
    ///
    /// The reference prototype fetched three.js from unpkg and evaluated it from a Blob URL when the
    /// vendored copy was missing. That path needs `script-src blob:` and a network an air-gapped
    /// colony does not have, and it fails OPEN — a console that quietly downloads and runs remote
    /// code is worse than one that refuses to render.
    ///
    /// Asserted over the shipped assets rather than over the loader that no longer exists, because
    /// the rule is about what the console DOES, and the next person to add a dependency will not
    /// read a deleted file's comment.
    /// </summary>
    [Theory]
    [InlineData("app.js")]
    [InlineData("colony-live.js")]
    [InlineData("colony-renderer.js")]
    [InlineData("colony-host.js")]
    [InlineData("colony-topology.js")]
    [InlineData("colony-hud.js")]
    [InlineData("index.html")]
    // The vendored bundle itself, because it is the asset that COULD have carried a fallback: the
    // prototype's loader is what fetched from unpkg, and a "vendored" copy that phones home on a
    // cache miss would satisfy every other test in this file.
    [InlineData("vendor.three.min.js")]
    public void NoConsoleAsset_LoadsCodeFromAnywhereButThisOrigin(string asset)
    {
        var text = ApiHost.LoadUiAsset(asset);

        Assert.False(string.IsNullOrWhiteSpace(text), $"'{asset}' is missing; this guard read nothing.");

        foreach (var forbidden in new[] { "unpkg.com", "cdn.jsdelivr", "cdnjs.cloudflare", "esm.sh", "skypack" })
            Assert.False(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"'{asset}' names {forbidden}. The console's one runtime dependency is vendored and "
              + "served from this origin; a remote fetch would need a CSP this colony does not have "
              + "and a network it may not have either.");

        Assert.False(text.Contains("createObjectURL(new Blob", StringComparison.Ordinal),
            $"'{asset}' evaluates script from a Blob URL. That needs `script-src blob:` and is how "
          + "the prototype's CDN fallback smuggled remote code past a same-origin policy.");
    }
}
