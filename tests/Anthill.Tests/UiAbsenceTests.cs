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
    public void EveryShippedAsset_IsEmbeddedAndFound(string asset)
    {
        var content = ApiHost.LoadUiAsset(asset);

        Assert.False(string.IsNullOrWhiteSpace(content),
            $"The embedded UI asset '{asset}' was not found. Phase 6 moved these to src/Anthill.UI/ "
          + "and pins each LogicalName in Anthill.Api.csproj — if that pinning is broken, the "
          + "console serves blank with no other symptom. (This assertion assumes a normal build; "
          + "the AnthillNoUi build deliberately has none of them, and is checked in CI instead.)");
    }
}
