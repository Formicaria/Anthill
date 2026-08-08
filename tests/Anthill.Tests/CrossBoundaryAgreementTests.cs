using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Anthill.Core.Pheromones;
using Anthill.Core.Verification;
using Anthill.SDK.Common;
using Anthill.SDK.Contracts;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// Values produced on one side of a boundary, consumed on the other. v3.8.32.
///
/// THE DEFECT CLASS THIS EXISTS FOR
/// --------------------------------
/// An external review of v3.8.29 found five defects. Every one had passing tests over it, and every
/// one had the same shape: a test that BUILT ITS OWN INPUT in the form its own side expected, so the
/// two sides of a boundary were each verified against an assumption rather than against each other.
///
/// The clearest instance: <c>TaskOutcomeMapper</c> wrote <c>transient_provider_failure</c> into
/// <c>Task.FailureType</c>; <c>LearningAttribution</c> compared that field against
/// <c>TransientProviderFailure</c> with <c>OrdinalIgnoreCase</c>, which normalises casing and not
/// underscores. The environmental-failure set matched NOTHING for six releases, so every provider
/// outage was charged as a negative pheromone trail against whichever ant held the task — the exact
/// bug v3.8.26 was written to prevent. The test passed because it fed
/// <c>nameof(FailureClass.TransientProviderFailure)</c>, a value production never writes there.
///
/// THE RULE
/// --------
/// A test for a cross-boundary value must obtain that value FROM THE PRODUCER. Never construct it.
/// If constructing it is unavoidable, the test proves the consumer parses a literal, and it must say
/// so in its name rather than implying it proved the wiring.
///
/// Everything below either drives a real producer into a real consumer, or reads the source to prove
/// no third representation has appeared.
/// </summary>
public class CrossBoundaryAgreementTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.GetFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// Source with comments blanked out. v3.8.33 moved the implementation to
    /// <see cref="SourceText.CodeOnly"/> — a third near-copy was about to be written for the model
    /// guard, which is the same shape as the three patch appliers this release collapsed.
    ///
    /// The reason it exists is worth keeping here: this codebase documents its defects IN COMMENTS,
    /// by quoting the offending expression, so a guard that greps raw text reports the paragraph
    /// explaining why <c>cls.ToString()</c> was wrong as an instance of <c>cls.ToString()</c>. That
    /// happened on the first run. The fix is to make the guard read what it claims to read, never to
    /// reword the explanation.
    /// </summary>
    private static string CodeOnly(string source) => SourceText.CodeOnly(source);

    public static TheoryData<FailureClass> AllFailureClasses()
    {
        var data = new TheoryData<FailureClass>();
        foreach (var cls in Enum.GetValues<FailureClass>()) data.Add(cls);
        return data;
    }

    // ---------------------------------------------------------------------------------------
    // 1. The failure-class chain: ant → mapper → task → attribution.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE test that should have existed since v3.8.26, driving the whole chain end to end.
    ///
    /// A real <c>AntExecutionResult</c> goes through the real <c>TaskOutcomeMapper</c>, its output is
    /// placed on a task exactly as <c>ApplyNonCompletingOutcome</c> places it, and the real
    /// <c>LearningAttribution</c> reads it. No string is written by this test.
    ///
    /// Before v3.8.32 this failed for all six environmental classes.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFailureClasses))]
    public void AFailureTravelsFromTheAntToAttribution_WithoutChangingMeaning(FailureClass cls)
    {
        // PRODUCER — what an ant really returns.
        var execution = AntExecutionResult.Failed(cls, "the provider fell over");

        // TRANSPORT — the real mapper, not a literal.
        var decision = TaskOutcomeMapper.Map(execution);

        // The task, filled exactly as ApplyNonCompletingOutcome fills it.
        var task = new DomainTask
        {
            Title = "t", AssignedAnt = "coder", TaskType = "code_change",
            Status = TaskStatus.Failed,
            FailureReason = decision.Reason,
            FailureType = decision.FailureType,
        };

        // CONSUMER — the real attribution rule.
        var attribution = LearningAttribution.For(task, missionVerified: false);

        var environmental = LearningAttribution.NotTheWorkersFault.Contains(cls);
        Assert.Equal(
            environmental ? LearningAttribution.Attribution.Neutral : LearningAttribution.Attribution.Negative,
            attribution);

        // And the delta follows, because a neutral attribution that still moved the trail would be
        // the same bug one layer down.
        if (environmental) Assert.Equal(0.0, LearningAttribution.DeltaFor(attribution));
        else Assert.True(LearningAttribution.DeltaFor(attribution) < 0);
    }

    /// <summary>
    /// The failing half stated separately, so a future implementation that makes EVERYTHING neutral
    /// cannot pass the theory above by being uniformly blameless.
    /// </summary>
    [Fact]
    public void TheChain_StillBlamesTheDoerForItsOwnFailures()
    {
        foreach (var cls in new[] { FailureClass.ValidationFailure, FailureClass.InternalDefect, FailureClass.VerificationFailure })
        {
            var decision = TaskOutcomeMapper.Map(AntExecutionResult.Failed(cls, "bad output"));
            var task = new DomainTask { Title = "t", AssignedAnt = "coder", Status = TaskStatus.Failed, FailureType = decision.FailureType };

            Assert.Equal(LearningAttribution.Attribution.Negative,
                LearningAttribution.For(task, missionVerified: false));
        }
    }

    /// <summary>
    /// The specific pair of strings that disagreed, pinned by value. This is the regression that ran
    /// unnoticed for six releases, so it is worth naming rather than only covering by theory.
    /// </summary>
    [Fact]
    public void TheTwoHistoricalSpellings_BothResolveToTheSameClass()
    {
        Assert.Equal("transient_provider_failure", FailureClassNames.Wire(FailureClass.TransientProviderFailure));

        Assert.True(FailureClassNames.TryParse("transient_provider_failure", out var fromWire));
        Assert.True(FailureClassNames.TryParse("TransientProviderFailure", out var fromEnumName));

        Assert.Equal(FailureClass.TransientProviderFailure, fromWire);
        Assert.Equal(FailureClass.TransientProviderFailure, fromEnumName);
    }

    /// <summary>Round-trip for every member: the canonical form must always parse back to itself.</summary>
    [Theory]
    [MemberData(nameof(AllFailureClasses))]
    public void EveryFailureClass_RoundTripsThroughItsWireForm(FailureClass cls)
    {
        Assert.True(FailureClassNames.TryParse(FailureClassNames.Wire(cls), out var back));
        Assert.Equal(cls, back);

        // Rows written before v3.8.32 hold the enum name. Upgrading must not drop their class.
        Assert.True(FailureClassNames.TryParse(cls.ToString(), out var legacy));
        Assert.Equal(cls, legacy);
    }

    /// <summary>Distinct names, or two classes would collapse into one bucket in every report.</summary>
    [Fact]
    public void WireNames_AreUniqueAndLowerCase()
    {
        Assert.Equal(Enum.GetValues<FailureClass>().Length, FailureClassNames.AllWire.Distinct(StringComparer.Ordinal).Count());
        Assert.All(FailureClassNames.AllWire, n => Assert.Equal(n.ToLowerInvariant(), n));
    }

    /// <summary>
    /// An unclassified or unknown failure must NOT parse. The runtime writes untyped failure types
    /// (<c>missing_ant</c>, <c>execution_error</c>, <c>worker_runtime_denied</c>) into the same field,
    /// and a parser that guessed at those would silently reclassify them.
    /// </summary>
    [Theory]
    [InlineData("missing_ant")]
    [InlineData("execution_error")]
    [InlineData("worker_runtime_denied")]
    [InlineData("ant_permission_denied")]
    [InlineData("")]
    [InlineData(null)]
    public void UntypedRuntimeFailureTypes_DoNotResolveToAClass(string? text) =>
        Assert.False(FailureClassNames.TryParse(text, out _));

    /// <summary>
    /// ...and an untyped failure is therefore charged to the doer, not excused. Absence of a
    /// classification is not evidence of an environmental cause.
    /// </summary>
    [Fact]
    public void AnUntypedRuntimeFailure_IsStillTheDoers()
    {
        var task = new DomainTask { Title = "t", AssignedAnt = "coder", Status = TaskStatus.Failed, FailureType = "execution_error" };

        Assert.Equal(LearningAttribution.Attribution.Negative, LearningAttribution.For(task, missionVerified: false));
    }

    /// <summary>
    /// THE DETECTOR. No source file may stringify a <c>FailureClass</c> except through
    /// <see cref="FailureClassNames"/>.
    ///
    /// This is the guard whose absence allowed the original defect: three separate call sites reached
    /// for <c>.ToString()</c> on a value headed for storage, and a fourth wrote its own snake-case
    /// converter. Nothing connected them, so nothing could notice they disagreed.
    /// </summary>
    [Fact]
    public void NoSourceFile_StringifiesAFailureClassOutsideTheSharedConverter()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            if (Path.GetFileName(file) == "ToolVocabulary.cs") continue;   // where the converter lives

            var text = CodeOnly(File.ReadAllText(file));
            foreach (Match m in Regex.Matches(text, @"\b(?:Failure|Class|cls)\??\.(?:Class\??\.)?ToString\(\)"))
            {
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(Root(), file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A FailureClass reaching storage or comparison as `.ToString()` is how the "
            + "transient_provider_failure defect happened. Use FailureClassNames.Wire: "
            + string.Join(", ", offenders));
    }

    // ---------------------------------------------------------------------------------------
    // 1b. The same defect shape, everywhere else it could hide.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE GENERALISED GUARD. An enum with a custom wire form must never be read back by
    /// <c>Enum.TryParse</c>.
    ///
    /// This is the FailureClass defect stated as a rule. The codebase has two legitimate conventions
    /// for putting an enum in a database:
    ///
    /// <list type="bullet">
    /// <item><c>.ToString()</c> out, <c>Enum.TryParse</c> back — used by AttemptState,
    ///   WorkspaceState, EscalationPolicy, SkillStatus, ArtifactVisibility, RecoveryAction.</item>
    /// <item>A custom <c>Value()</c> out and a matching parser or <c>Value()</c> comparison back —
    ///   used by TaskStatus, MissionStatus, PatchStatus, PatchChangeType, ApprovalStatus,
    ///   ApprovalActionType, ObjectiveStatus.</item>
    /// </list>
    ///
    /// Both are fine. MIXING them is the bug, and mixing them is invisible for any enum whose member
    /// names contain no underscores — <c>Enum.TryParse("complete")</c> happily yields
    /// <c>MissionStatus.Complete</c>, so the mistake only bites on multi-word members like
    /// <c>TransientProviderFailure</c> and <c>patch_proposal</c>. That is exactly why it survived six
    /// releases: it worked for most values.
    ///
    /// A v3.8.32 sweep confirmed FailureClass was the only mismatched pair in the tree. This keeps it
    /// that way.
    /// </summary>
    [Fact]
    public void NoEnumWithACustomWireForm_IsReadBackWithEnumTryParse()
    {
        var enumsFile = Path.Combine(Root(), "src", "Anthill.Core", "Domain", "Enums.cs");
        Assert.True(File.Exists(enumsFile), "Enums.cs moved; this guard needs its new home");

        // Every type with a `Value(this X x)` extension — i.e. a wire form that is NOT ToString().
        var wireFormed = Regex.Matches(CodeOnly(File.ReadAllText(enumsFile)),
                @"static\s+string\s+Value\s*\(\s*this\s+(\w+)\s")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(wireFormed);   // the guard must not pass by finding nothing

        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var text = CodeOnly(File.ReadAllText(file));
            foreach (var type in wireFormed)
                foreach (Match m in Regex.Matches(text, $@"Enum\.TryParse\s*<\s*{Regex.Escape(type)}\s*>"))
                    offenders.Add($"{Path.GetRelativePath(Root(), file)}:{text[..m.Index].Count(c => c == '\n') + 1} ({type})");
        }

        Assert.True(offenders.Count == 0,
            "These read an enum with a custom wire form using Enum.TryParse, which understands only "
            + "the member NAME. It silently succeeds for single-word members and fails for the rest — "
            + "the transient_provider_failure defect exactly: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The wire forms that DO have explicit parsers round-trip. Without this the guard above could be
    /// satisfied by a parser that is simply wrong.
    /// </summary>
    [Fact]
    public void TheWireFormedEnums_RoundTripThroughTheirOwnParsers()
    {
        foreach (var status in Enum.GetValues<TaskStatus>())
            Assert.Equal(status, EnumExtensions.ParseTaskStatus(status.Value()));

        foreach (var kind in Enum.GetValues<PatchChangeType>())
            Assert.Equal(kind, EnumExtensions.ParsePatchChangeType(kind.Value()));

        foreach (var objective in Enum.GetValues<ObjectiveStatus>())
            Assert.Equal(objective, EnumExtensions.ParseObjectiveStatus(objective.Value()));
    }

    /// <summary>
    /// <c>PatchApply</c> understands the same change-type spellings <c>PatchChangeType.Value()</c>
    /// emits. The materializer and the sandbox both convert an enum to a string for it, and a
    /// disagreement there would refuse every patch as "unsupported change type".
    /// </summary>
    [Fact]
    public void PatchApply_UnderstandsTheWireSpellingsOfAddAndModify()
    {
        Assert.Equal(PatchApply.Add, PatchChangeType.Add.Value());
        Assert.Equal(PatchApply.Modify, PatchChangeType.Modify.Value());

        Assert.Equal(PatchApplyStatus.Created,
            PatchApply.Compute(PatchChangeType.Add.Value(), null, "x", null).Status);
        Assert.Equal(PatchApplyStatus.Modified,
            PatchApply.Compute(PatchChangeType.Modify.Value(), "a", "b", "xax").Status);
    }

    // ---------------------------------------------------------------------------------------
    // 2. Patch application: one semantics, three callers.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE materializer defect, stated as an observable fact about bytes.
    ///
    /// The verifier's sandbox used to write <c>new_content</c> over the entire file for a modify. So
    /// a patch changing one line of a hundred-line file produced a one-line file, and the build that
    /// "verified the patch" compiled a tree the operator's applier would never produce.
    ///
    /// This asserts the surrounding content SURVIVES — which is exactly what the old code destroyed.
    /// </summary>
    [Fact]
    public void MaterializingAModify_PreservesEverythingOutsideTheReplacedText()
    {
        var source = Path.Combine(Path.GetTempPath(), $"anthill-mat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        try
        {
            var original = "line one\nline two\nTARGET\nline four\nline five\n";
            File.WriteAllText(Path.Combine(source, "file.txt"), original);

            var set = new PatchSet
            {
                Proposals =
                {
                    new PatchProposal
                    {
                        FilePath = "file.txt",
                        ChangeType = PatchChangeType.Modify,
                        OldContent = "TARGET",
                        NewContent = "REPLACED",
                    },
                },
            };

            var result = PatchSetMaterializer.Materialize(set, source);
            Assert.True(result.Ok, result.Problem);
            using var materialized = result.Materialized!;

            var landed = File.ReadAllText(Path.Combine(materialized.Root, "file.txt"));

            Assert.Equal("line one\nline two\nREPLACED\nline four\nline five\n", landed);
            // The assertion that fails loudly on the old implementation.
            Assert.Contains("line five", landed);
            Assert.DoesNotContain("TARGET", landed);
        }
        finally { try { Directory.Delete(source, true); } catch { } }
    }

    /// <summary>
    /// The sandbox must REFUSE what the real applier refuses. A verification that passes for a patch
    /// which can never be applied is worse than no verification: it produces evidence for a tree that
    /// cannot exist.
    /// </summary>
    [Fact]
    public void MaterializingAnAmbiguousModify_IsRefusedJustAsApplyPatchToolWouldRefuseIt()
    {
        var source = Path.Combine(Path.GetTempPath(), $"anthill-mat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(Path.Combine(source, "file.txt"), "dup\nmiddle\ndup\n");

            var set = new PatchSet
            {
                Proposals =
                {
                    new PatchProposal
                    {
                        FilePath = "file.txt", ChangeType = PatchChangeType.Modify,
                        OldContent = "dup", NewContent = "changed",
                    },
                },
            };

            var result = PatchSetMaterializer.Materialize(set, source);

            Assert.False(result.Ok);
            Assert.Contains("unambiguous", result.Problem ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(source, true); } catch { } }
    }

    /// <summary>
    /// A modify with no <c>old_content</c> is refused rather than silently overwritten. Two of the
    /// three appliers used to treat it as a whole-file replacement; the operator's applier has always
    /// refused it, and that is the semantics that counts.
    /// </summary>
    [Fact]
    public void AModifyWithoutOldContent_IsRefusedEverywhere()
    {
        var outcome = PatchApply.Compute(PatchApply.Modify, oldContent: null, newContent: "x", currentContent: "existing");

        Assert.False(outcome.Ok);
        Assert.Equal(PatchApplyStatus.RefusedMissingOldContent, outcome.Status);
    }

    [Fact]
    public void AModifyAgainstAMissingFile_IsRefused() =>
        Assert.Equal(PatchApplyStatus.RefusedTargetMissing,
            PatchApply.Compute(PatchApply.Modify, "old", "new", currentContent: null).Status);

    [Fact]
    public void AModifyWhoseOldContentIsAbsent_IsRefused() =>
        Assert.Equal(PatchApplyStatus.RefusedOldContentNotFound,
            PatchApply.Compute(PatchApply.Modify, "nowhere", "new", currentContent: "abc").Status);

    /// <summary>An existing EMPTY file is a modify target, not a missing one. The two refuse
    /// differently, so conflating null with "" would report the wrong reason.</summary>
    [Fact]
    public void AnExistingEmptyFile_IsNotTreatedAsMissing() =>
        Assert.Equal(PatchApplyStatus.RefusedOldContentNotFound,
            PatchApply.Compute(PatchApply.Modify, "old", "new", currentContent: "").Status);

    [Fact]
    public void AnAddOntoANewPath_Creates() =>
        Assert.Equal(PatchApplyStatus.Created,
            PatchApply.Compute(PatchApply.Add, null, "hello", currentContent: null).Status);

    /// <summary>An add onto an existing file overwrites — recorded distinctly so the backup that
    /// makes it safe is visible in the audit trail rather than implied.</summary>
    [Fact]
    public void AnAddOntoAnExistingPath_OverwritesAndSaysSo() =>
        Assert.Equal(PatchApplyStatus.Overwrote,
            PatchApply.Compute(PatchApply.Add, null, "hello", currentContent: "was here").Status);

    [Fact]
    public void AnUnsupportedChangeType_IsRefusedRatherThanTreatedAsAModify() =>
        Assert.Equal(PatchApplyStatus.RefusedUnsupportedChangeType,
            PatchApply.Compute("delete", "old", "new", "current").Status);

    /// <summary>
    /// Occurrence counting is NON-OVERLAPPING, and the uniqueness rule depends on it. "aa" occurs
    /// twice in "aaaa", not three times; counting overlaps would refuse legitimate patches.
    /// </summary>
    [Fact]
    public void OccurrenceCounting_DoesNotCountOverlaps() =>
        Assert.Equal(2, PatchApply.CountOccurrences("aaaa", "aa"));

    /// <summary>
    /// THE DETECTOR for this boundary: no file may write patch content to disk without going through
    /// the shared applier. Three implementations is how the verifier came to check different bytes
    /// from the ones the operator would get.
    /// </summary>
    [Fact]
    public void NoSourceFile_ImplementsItsOwnPatchApplication()
    {
        var allowed = new[] { "PatchApply.cs" };
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            if (allowed.Contains(Path.GetFileName(file))) continue;
            var text = CodeOnly(File.ReadAllText(file));

            // The signature of a hand-rolled applier: locating old content in order to splice it.
            if (Regex.IsMatch(text, @"IndexOf\s*\(\s*(?:p\.)?[Oo]ldContent")
                || Regex.IsMatch(text, @"Contains\s*\(\s*(?:p\.)?[Oo]ldContent"))
                offenders.Add(Path.GetRelativePath(Root(), file));
        }

        Assert.True(offenders.Count == 0,
            "Patch application semantics must live only in PatchApply — a second copy is how the "
            + "sandbox came to verify bytes the operator's applier would never produce: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The patch-set hash covers OLD content too. Two sets replacing different text with the same new
    /// text are different changes producing different trees; hashing them alike would let a
    /// verification verdict be bound to a change it never judged.
    /// </summary>
    [Fact]
    public void ThePatchSetHash_DistinguishesDifferentOldContent()
    {
        PatchSet Set(string oldContent) => new()
        {
            Proposals =
            {
                new PatchProposal
                {
                    Id = "fixed-id", FilePath = "a.txt", ChangeType = PatchChangeType.Modify,
                    OldContent = oldContent, NewContent = "same new content",
                },
            },
        };

        Assert.NotEqual(
            PatchSetMaterializer.HashPatchSet(Set("first")),
            PatchSetMaterializer.HashPatchSet(Set("second")));
    }
}
