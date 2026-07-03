using System.Text.Json;
using Anthill.Api;
using Anthill.Core.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v1.8.18.1 — invalid-UTF-16 hardening. `System.Text.Json` throws "Cannot transcode invalid UTF-16"
/// on a lone surrogate during response serialization (after the endpoint returned), surfacing as an
/// uncatchable empty HTTP 500 — the Patch Center bug. <see cref="TextUtil.SanitizeUtf16"/> and the
/// central <see cref="ApiJson.SanitizeJson"/> scrub such strings so every JSON endpoint is fail-safe.
/// </summary>
public class JsonSafetyTests
{
    [Fact]
    public void CleanStringIncludingValidEmoji_IsUnchanged()
    {
        var emoji = "hello 🌍 world"; // valid surrogate pair (🌍)
        Assert.Equal(emoji, TextUtil.SanitizeUtf16(emoji));
        Assert.Equal("plain text", TextUtil.SanitizeUtf16("plain text"));
    }

    [Fact]
    public void LoneHighSurrogate_IsReplaced()
    {
        var bad = "before\uD800after";
        var cleaned = TextUtil.SanitizeUtf16(bad);
        Assert.Equal("before�after", cleaned);
        Assert.False(cleaned.Any(char.IsSurrogate));
    }

    [Fact]
    public void LoneLowSurrogate_IsReplaced()
    {
        Assert.Equal("x�", TextUtil.SanitizeUtf16("x\uDC00"));
    }

    [Fact]
    public void SanitizedString_SerializesWithoutThrowing()
    {
        var cleaned = TextUtil.SanitizeUtf16("reason \uD800 end"); // lone surrogate scrubbed
        var json = JsonSerializer.Serialize(cleaned);             // must not throw
        Assert.Contains("reason", json);
    }

    [Fact]
    public void SanitizeJson_DeepScrubsNestedStrings_ThenSerializes()
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = "clean",
            ["bad"] = "lone\uD800surrogate",
            ["count"] = 42,
            ["list"] = new List<object?>
            {
                "fine",
                "low\uDC00here",
                new Dictionary<string, object?> { ["nested"] = "high\uD800" },
            },
        };
        var cleaned = ApiJson.SanitizeJson(payload);
        var json = JsonSerializer.Serialize(cleaned); // would throw on the raw payload
        Assert.DoesNotContain('\uD800', json);
        Assert.DoesNotContain('\uDC00', json);
        Assert.Contains("clean", json);
        Assert.Contains("42", json);
    }

    [Fact]
    public void SanitizeJson_PreservesByteArraysAndScalars()
    {
        var bytes = new byte[] { 1, 2, 3 };
        Assert.Same(bytes, ApiJson.SanitizeJson(bytes));  // byte[] passes through (→ base64), not expanded
        Assert.Equal(true, ApiJson.SanitizeJson(true));
        Assert.Null(ApiJson.SanitizeJson(null));
    }
}
