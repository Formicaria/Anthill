using Anthill.Modules.Reasoning;
using Anthill.SDK.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.93 — CHARACTERIZATION of the agent-CLI boundary: for each role shape × operator policy,
/// exactly what reaches a spawned agent process — argv, settings payload, working directory.
///
/// Recorded at the PURE-FUNCTION layer (BuildArgs / BuildAccessArgs / BuildLocalSettingsJson /
/// EffectiveWorkingDirectory are pure; AgentCliProvider concatenates them and starts the process),
/// against the real catalog entry for Claude Code, with no process started.
///
/// HONEST QUALIFICATION NOTE, per the release brief: this fixture is NOT the live gate. No vendor
/// CLI runs here, so nothing below proves that Claude Code interprets these flags as documented —
/// only that Anthill emits exactly these flags for exactly these inputs, and keeps emitting them.
/// A live end-to-end run through an installed agent CLI remains an operator-driven qualification
/// step and is recorded as NOT RUN by this suite. When that live run happens, its transcript — not
/// this file — is the evidence that the boundary works; this file is the evidence that the boundary
/// cannot silently change shape between live runs.
/// </summary>
public class CliBoundaryCharacterizationTests
{
    private static AgentCli ClaudeCode() =>
        AgentCliCatalog.All.Single(a => a.Id == "agent:claude-code");

    private static AgentAccessScope.Context Mission(string policy, bool roleMayWrite) =>
        new(policy, new[] { "/repos/project" }, ConfinedWorkspace: true,
            WorkingDirectory: "/tmp/mission-ws", RoleMayWrite: roleMayWrite);

    /// <summary>The full matrix, one row per (policy, role-writes) cell, argv recorded exactly.</summary>
    public static TheoryData<string, bool, string[]> Cells => new()
    {
        // A writing role (coder): the operator's policy translates, verbatim and bounded.
        { "ask", true, new[] { "--permission-mode", "acceptEdits", "--add-dir", "/repos/project" } },
        { "autoapprove", true, new[]
            { "--permission-mode", "acceptEdits", "--allowedTools",
              "Edit,Write,Bash(dotnet:*),Bash(node:*),Bash(npm:*),Bash(git status:*),Bash(git diff:*),Bash(git log:*)",
              "--add-dir", "/repos/project" } },
        // v0.3.8.95 — BYPASS IS BOUNDED INSIDE A CONFINED WORKSPACE (this matrix's Mission context
        // is confined, as every mission's is). The skip flag removes the vendor's entire
        // permission layer while the process inherits the host's environment; Skip-all's promise
        // is about PROMPTS, and the autoapprove posture already delivers promptless edits plus the
        // bounded tool set. The unrestricted flag survives only for an UNCONFINED context — a road
        // no mission takes — pinned by ActingMissionPipelineTests, not by this matrix.
        { "bypass", true, new[]
            { "--permission-mode", "acceptEdits", "--allowedTools",
              "Edit,Write,Bash(dotnet:*),Bash(node:*),Bash(npm:*),Bash(git status:*),Bash(git diff:*),Bash(git log:*)",
              "--add-dir", "/repos/project" } },
        // A read-only role (researcher/builder/verifier): reach only, under every policy.
        { "ask", false, new[] { "--add-dir", "/repos/project" } },
        { "autoapprove", false, new[] { "--add-dir", "/repos/project" } },
        { "bypass", false, new[] { "--add-dir", "/repos/project" } },
    };

    [Theory]
    [MemberData(nameof(Cells))]
    public void TheAccessArgv_IsExactlyThis_PerCell(string policy, bool roleMayWrite, string[] expected)
    {
        var args = AgentCliCatalog.BuildAccessArgs(ClaudeCode(), Mission(policy, roleMayWrite));
        Assert.Equal(expected, args);
    }

    /// <summary>
    /// The working directory an acting agent stands in is the ambient flow's own — the mission
    /// workspace — never Anthill's cwd, and a writing agent with no directory at all is REFUSED
    /// before the process starts (AgentCliProvider.Confinement). The resolution is recorded here;
    /// the refusal has its own tests in AgentCliTests.
    /// </summary>
    [Fact]
    public void TheWorkingDirectory_IsTheMissionWorkspace()
    {
        using (AgentAccessScope.Enter("ask", null, confinedWorkspace: true,
                   workingDirectory: "/tmp/mission-ws"))
            Assert.Equal("/tmp/mission-ws", AgentAccessScope.Current!.WorkingDirectory);
    }

    /// <summary>
    /// The settings payload — the second channel of the same policy — recorded per cell: a writing
    /// role's bypass/autoapprove carries the bounded tool list; a read-only role's carries reach
    /// and nothing executable, under every policy.
    /// </summary>
    [Theory]
    [InlineData("bypass", true, true)]
    [InlineData("autoapprove", true, true)]
    [InlineData("bypass", false, false)]
    [InlineData("autoapprove", false, false)]
    [InlineData("ask", false, false)]
    public void TheSettingsPayload_MatchesTheCell(string policy, bool roleMayWrite, bool expectTools)
    {
        var json = AgentCliCatalog.BuildLocalSettingsJson(Mission(policy, roleMayWrite));

        Assert.NotNull(json);   // every cell here has at least the directory grant
        Assert.Contains("/repos/project", json);
        if (expectTools)
        {
            Assert.Contains("\"Edit\"", json);
            Assert.Contains("Bash(dotnet:*)", json);
        }
        else
        {
            Assert.DoesNotContain("\"Edit\"", json);
            Assert.DoesNotContain("\"Write\"", json);
            Assert.DoesNotContain("Bash(", json);
        }
    }
}
