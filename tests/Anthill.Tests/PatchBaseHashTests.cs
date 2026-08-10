using Anthill.Core.Domain;
using Anthill.Core.Verification;
using Anthill.SDK.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A patch built against a stale read is refused. v0.3.8.37.
///
/// `AUTONOMY-10.md` called this "the largest single gap in Phase 1", and the reason is that
/// `old_content` matching looks like it covers the case and does not. It proves the FRAGMENT is
/// still present; it says nothing about whether the rest of the file moved on underneath it. A coder
/// reads a file, reasons about the whole of it, proposes an edit — and by the time the patch applies
/// the surrounding lines can be gone while the fragment survives. The edit lands, cleanly, into a
/// file nobody looked at.
///
/// The base hash is of CONTENT rather than a git revision, deliberately: the coder reads a working
/// tree that may hold uncommitted changes no revision names. Hashing what was actually read is the
/// only thing that answers "is this still the text the patch was reasoned about".
///
/// NULL IS ACCEPTED, and that is a staged decision rather than an oversight. Every proposal written
/// before this release carries no hash; refusing them all would turn a safety improvement into an
/// outage. `WorkspaceChangeSet` — the producer that actually reads files — records one from now on.
/// </summary>
public class PatchBaseHashTests
{
    private const string Original = "line one\nline two\nTARGET\nline four\n";

    // ---------------------------------------------------------------------------------------
    // The rule
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AMatchingBase_Applies()
    {
        var outcome = PatchApply.Compute(PatchApply.Modify, "TARGET", "REPLACED", Original,
            PatchApply.HashOf(Original));

        Assert.True(outcome.Ok);
        Assert.Equal("line one\nline two\nREPLACED\nline four\n", outcome.Content);
    }

    /// <summary>
    /// THE case. The fragment is still there and the file has moved on — exactly the situation
    /// `old_content` matching cannot see, which is why it was the largest Phase 1 gap.
    /// </summary>
    [Fact]
    public void AStalePatchIsRefused_EvenWhenOldContentStillMatches()
    {
        var built = PatchApply.HashOf(Original);
        var moved = Original + "line five ADDED BY SOMEONE ELSE\n";

        // old_content is present and unique in the new text — the pre-v0.3.8.37 check passes.
        Assert.Equal(1, PatchApply.CountOccurrences(moved, "TARGET"));

        var outcome = PatchApply.Compute(PatchApply.Modify, "TARGET", "REPLACED", moved, built);

        Assert.False(outcome.Ok);
        Assert.Equal(PatchApplyStatus.RefusedStaleBase, outcome.Status);
    }

    /// <summary>The refusal names the remedy, and both hashes, so an operator can compare by eye.</summary>
    [Fact]
    public void TheStaleRefusal_SaysWhatToDoAboutIt()
    {
        var outcome = PatchApply.Compute(PatchApply.Modify, "TARGET", "X", Original + "drift\n",
            PatchApply.HashOf(Original));

        Assert.Contains("changed since this patch was built", outcome.Reason);
        Assert.Contains("propose again", outcome.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PatchApply.HashOf(Original)[..12], outcome.Reason);
    }

    /// <summary>
    /// A missing base hash still applies. Staged rollout, stated as a test so nobody "tightens" it
    /// into an outage for every proposal written before this release.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoRecordedBase_StillApplies(string? baseHash)
    {
        var outcome = PatchApply.Compute(PatchApply.Modify, "TARGET", "REPLACED", Original, baseHash);

        Assert.True(outcome.Ok);
    }

    /// <summary>
    /// The base is checked BEFORE the fragment search, so a stale file reports staleness rather than
    /// "old_content not found". They have different remedies — rebuild the patch versus fix the
    /// fragment — and the wrong one sends the coder to correct something that was never wrong.
    /// </summary>
    [Fact]
    public void StalenessIsReportedBeforeAMissingFragment()
    {
        var outcome = PatchApply.Compute(PatchApply.Modify, "GONE", "X", "completely different text",
            PatchApply.HashOf(Original));

        Assert.Equal(PatchApplyStatus.RefusedStaleBase, outcome.Status);
    }

    /// <summary>Hex, lowercase, stable, and different for different content — or comparisons lie.</summary>
    [Fact]
    public void TheHashIsStableAndDiscriminating()
    {
        Assert.Equal(PatchApply.HashOf(Original), PatchApply.HashOf(Original));
        Assert.NotEqual(PatchApply.HashOf(Original), PatchApply.HashOf(Original + " "));
        Assert.Equal(64, PatchApply.HashOf(Original).Length);
        Assert.Equal(PatchApply.HashOf(Original).ToLowerInvariant(), PatchApply.HashOf(Original));
        // Null and empty are the same base: an absent file and an empty one both read as no content.
        Assert.Equal(PatchApply.HashOf(null), PatchApply.HashOf(""));
    }

    /// <summary>An `add` that creates a file has no base to be stale against.</summary>
    [Fact]
    public void AnAddOntoNothing_IgnoresTheBaseHash()
    {
        var outcome = PatchApply.Compute(PatchApply.Add, null, "brand new", currentContent: null,
            expectedBaseHash: PatchApply.HashOf("something else entirely"));

        Assert.Equal(PatchApplyStatus.Created, outcome.Status);
    }

    // ---------------------------------------------------------------------------------------
    // It reaches the verifier, which is the half that would otherwise attest to a stale tree
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The materializer refuses a stale patch too. A sandbox that accepts one compiles a tree the
    /// operator's applier would reject — the v3.8.23 defect in a new disguise, and the reason this
    /// check had to reach all three appliers rather than only the tool.
    /// </summary>
    [Fact]
    public void TheVerifiersSandbox_RefusesAStalePatch()
    {
        var source = Path.Combine(Path.GetTempPath(), $"anthill-base-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(Path.Combine(source, "file.txt"), Original + "drifted since\n");

            var set = new PatchSet
            {
                Proposals =
                {
                    new PatchProposal
                    {
                        FilePath = "file.txt", ChangeType = PatchChangeType.Modify,
                        OldContent = "TARGET", NewContent = "REPLACED",
                        BaseHash = PatchApply.HashOf(Original),   // built against the older text
                    },
                },
            };

            var result = PatchSetMaterializer.Materialize(set, source);

            Assert.False(result.Ok);
            Assert.Contains("changed since this patch was built", result.Problem ?? "");
        }
        finally { try { Directory.Delete(source, true); } catch { } }
    }

    /// <summary>...and applies one whose base still matches, or the check would just block everything.</summary>
    [Fact]
    public void TheVerifiersSandbox_AppliesAFreshPatch()
    {
        var source = Path.Combine(Path.GetTempPath(), $"anthill-base-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(Path.Combine(source, "file.txt"), Original);

            var set = new PatchSet
            {
                Proposals =
                {
                    new PatchProposal
                    {
                        FilePath = "file.txt", ChangeType = PatchChangeType.Modify,
                        OldContent = "TARGET", NewContent = "REPLACED",
                        BaseHash = PatchApply.HashOf(Original),
                    },
                },
            };

            var result = PatchSetMaterializer.Materialize(set, source);
            Assert.True(result.Ok, result.Problem);
            using var materialized = result.Materialized!;

            Assert.Contains("REPLACED", File.ReadAllText(Path.Combine(materialized.Root, "file.txt")));
        }
        finally { try { Directory.Delete(source, true); } catch { } }
    }

    /// <summary>
    /// The producer that reads files records the base. Without this the field exists, everything
    /// passes, and nothing is ever actually checked — the implemented-tested-unreachable pattern this
    /// repository has now hit ten times.
    /// </summary>
    [Fact]
    public void TheWorkspaceProducer_RecordsTheBaseItRead()
    {
        var source = SourceText.RepoRoot();
        var text = File.ReadAllText(Path.Combine(source, "src", "Anthill.Core", "Workspaces", "WorkspaceChangeSet.cs"));

        Assert.Contains("BaseHash = PatchApply.HashOf(oldContent)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE reachability proof: the hash survives the database.
    ///
    /// Everything above passes even if `base_hash` never reaches the operator's applier — the column
    /// could be missing, the INSERT could omit it, the read could drop it, and every unit test here
    /// would still be green while the check never fired in production. That is precisely the shape
    /// this repository has shipped ten times, so the round trip is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void TheBaseHash_SurvivesTheDatabaseAndReachesTheApplier()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"anthill-basehash-{Guid.NewGuid():N}.db");
        try
        {
            using var memory = new Anthill.Core.Memory.SqliteMemory(dbPath);

            // A real task, because patch_proposals has a foreign key to tasks(id) and
            // foreign_keys is ON. Copied from how PatchCenterTests builds one rather than invented —
            // a fixture that cannot be persisted proves nothing about persistence.
            var task = new Anthill.Core.Domain.Task
            {
                Title = "propose a patch", AssignedAnt = "coder", TaskType = "code_change",
            };
            var mission = new Mission { Goal = "record a base hash", Tasks = { task } };
            memory.SaveMission(mission);

            var expected = PatchApply.HashOf(Original);
            var set = new PatchSet
            {
                MissionId = mission.Id,
                TaskId = task.Id,
                Summary = "base hash round trip",
                Proposals =
                {
                    new PatchProposal
                    {
                        FilePath = "file.txt", ChangeType = PatchChangeType.Modify,
                        OldContent = "TARGET", NewContent = "REPLACED", BaseHash = expected,
                    },
                },
            };
            memory.SavePatchSet(set);

            // Read it back exactly as the apply path does — GetPatchProposal, then the raw column.
            var row = memory.GetPatchProposal(set.Proposals[0].Id);

            Assert.NotNull(row);
            Assert.Equal(expected, row!.GetValueOrDefault("base_hash") as string);
        }
        finally { try { File.Delete(dbPath); } catch { } }
    }
}
