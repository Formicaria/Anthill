using Anthill.Api;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Mission-report readability (v1.8.14.1): the coder's raw JSON patch sets are translated into
/// plain-English "Proposed <change> to <file>: <reason>" lines for the report; prose from other
/// ants passes through untouched; malformed JSON falls back to the raw text instead of erroring.
/// </summary>
public class ReportTests
{
    [Fact]
    public void CoderJson_TranslatesToPlainEnglish()
    {
        const string raw = """
        {
          "summary": "Harden the token check.",
          "proposals": [
            { "file_path": "src/Auth/TokenGuard.cs", "change_type": "modify",
              "reason": "Use constant-time comparison.", "risk": "low",
              "old_content": "a == b", "new_content": "FixedTimeEquals(a, b)", "requires_approval": true }
          ]
        }
        """;
        var readable = ApiHost.ReadableTaskOutput("coder", raw);
        Assert.Contains("Harden the token check.", readable);
        Assert.Contains("Proposed modify to src/Auth/TokenGuard.cs: Use constant-time comparison.", readable);
        Assert.DoesNotContain("old_content", readable); // no raw JSON internals leak through
    }

    [Fact]
    public void CoderJson_EmptyProposals_SaysSoInEnglish()
    {
        var readable = ApiHost.ReadableTaskOutput("coder", """{ "summary": "Nothing to change.", "proposals": [] }""");
        Assert.Contains("Nothing to change.", readable);
        Assert.Contains("No file changes were proposed.", readable);
    }

    [Fact]
    public void MalformedCoderOutput_FallsBackToRawText()
    {
        const string raw = "The model rambled instead of returning JSON.";
        Assert.Equal(raw, ApiHost.ReadableTaskOutput("coder", raw));
    }

    [Fact]
    public void ProseFromOtherAnts_PassesThrough()
    {
        const string prose = "The colony's dependency manifest is current; no CVEs affect pinned versions.";
        Assert.Equal(prose, ApiHost.ReadableTaskOutput("builder", prose));
        Assert.Equal("", ApiHost.ReadableTaskOutput("researcher", "  "));
    }
}
