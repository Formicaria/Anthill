using Anthill.Api;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Phase 6's exit gate, made executable. v3.8.17.
///
/// The plan wrote it as a manual step — "boot the API with the UI assets absent; it must still serve
/// the API" — which is the kind of gate that is performed once, on the day it is written, and never
/// again. It is also the criterion the whole phase exists to satisfy: "the core runs without UI".
///
/// The degradation lives in exactly one method, <c>ApiHost.LoadUiAsset</c>, so the gate does not
/// need a running host to be checked. What matters is that a missing asset is a MISSING ASSET and
/// not an exception: the console goes blank, the API keeps answering. An asset lookup that threw
/// would take the whole host down at startup, and it would do so only in the deployment where the
/// assets were stripped — which is to say, in production and never in a test.
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
    /// And the assets that DO ship are still found after phase 6 moved them out of the API project.
    ///
    /// This is the other half of the move's risk, and the more dangerous half. `LoadUiAsset` matches
    /// by resource-name SUFFIX, so a move that changed the resource name prefix would still have
    /// worked, and one that changed the suffix would have served an empty console with no build
    /// error and no failing test. The csproj pins each `LogicalName` for that reason; this asserts
    /// the pinning holds.
    /// </summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("app.js")]
    [InlineData("mission-thread.js")]
    [InlineData("dashboard-grid.js")]
    [InlineData("dashboard-grid.css")]
    public void EveryShippedAsset_IsStillEmbeddedAndFound(string asset)
    {
        var content = ApiHost.LoadUiAsset(asset);

        Assert.False(string.IsNullOrWhiteSpace(content),
            $"The embedded UI asset '{asset}' was not found. Phase 6 moved these to src/Anthill.UI/ "
          + "and pins each LogicalName in Anthill.Api.csproj — if that pinning is broken, the "
          + "console serves blank with no other symptom.");
    }
}
