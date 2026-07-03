using Anthill.Core.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Lenient JSON extraction (v1.8.15.6) — the fix for the flood of coder
/// <c>patch_proposal_parse_failed</c> events. Small local models emit JSON with raw newlines inside
/// string values (which strict JSON rejects: "'0x0A' is invalid within a JSON string"), plus
/// trailing commas and code fences. ExtractJsonObject must recover all of these.
/// </summary>
public class JsonRepairTests
{
    [Fact]
    public void RawNewlineInsideStringValue_IsRecovered()
    {
        // A literal newline inside new_content — exactly what the coder produced live.
        var raw = "{\"summary\":\"ok\",\"new_content\":\"line one\nline two\"}";
        var obj = Json.ExtractJsonObject(raw);
        Assert.Equal("line one\nline two", obj["new_content"]!.GetValue<string>());
    }

    [Fact]
    public void RawTabAndCarriageReturn_AreRecovered()
    {
        var raw = "{\"a\":\"col1\tcol2\rnext\"}";
        var obj = Json.ExtractJsonObject(raw);
        Assert.Equal("col1\tcol2\rnext", obj["a"]!.GetValue<string>());
    }

    [Fact]
    public void TrailingCommasAndCodeFences_AreTolerated()
    {
        var raw = "```json\n{\"summary\":\"s\",\"proposals\":[],}\n```";
        var obj = Json.ExtractJsonObject(raw);
        Assert.Equal("s", obj["summary"]!.GetValue<string>());
    }

    [Fact]
    public void SurroundingProse_IsStripped()
    {
        var raw = "Sure, here is the patch:\n{\"summary\":\"done\",\n\"new_content\":\"a\nb\"}\nHope that helps!";
        var obj = Json.ExtractJsonObject(raw);
        Assert.Equal("done", obj["summary"]!.GetValue<string>());
    }

    [Fact]
    public void AlreadyEscapedContent_IsUnchanged()
    {
        // A well-formed response must round-trip without the repair mangling valid escapes.
        var raw = "{\"new_content\":\"line1\\nline2\\ttab\"}";
        var obj = Json.ExtractJsonObject(raw);
        Assert.Equal("line1\nline2\ttab", obj["new_content"]!.GetValue<string>());
    }

    [Fact]
    public void RepairLeavesControlCharsOutsideStringsAlone()
    {
        // Newlines between tokens (outside string values) are legal JSON whitespace — untouched.
        Assert.Equal("{\n  \"a\": \"b\"\n}", Json.RepairJsonControlChars("{\n  \"a\": \"b\"\n}"));
    }
}
