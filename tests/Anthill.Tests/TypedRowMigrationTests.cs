using System.Text.RegularExpressions;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE UNTYPED SURFACE ONLY EVER SHRINKS. v0.3.8.113, PLAN.md §2b `.113`.
///
/// WHY A RATCHET AND NOT A DEADLINE. `Dictionary&lt;string, object?&gt;` is on fifty public methods
/// of the store and read by a hundred consumer files. That is not one release, and PLAN has said so
/// since the item was written: "one slice at a time, each slice green before the next." A plan that
/// says that and enforces nothing is a plan that describes an intention — the sentence
/// `EventVocabularyTests` opens with, pointed at a migration instead of a vocabulary.
///
/// So the count is pinned, and it can only fall. A release that types a slice lowers the number in
/// the same commit; a release that adds a new untyped reader fails here and has to say why. That
/// turns "one slice at a time" from a promise into a property.
///
/// WHAT AN UNTYPED READER ACTUALLY COSTS, because the signature is the symptom and not the disease.
/// The approvals slice found it: `GetApprovalRequest` unprotected `decision_note` and
/// `GetApprovalForTarget` did not, so the same column came back as plaintext through one reader and
/// as ciphertext through the other. With a row-shaped API there is nowhere for "how a row becomes an
/// approval" to live, so each reader answers it again — and four answers to one question is defect
/// class 5, in the layer everything else reads.
/// </summary>
public class TypedRowMigrationTests
{
    /// <summary>
    /// Public members of <see cref="SqliteMemory"/> whose signature still hands a caller a row.
    ///
    /// Read from the TYPE by reflection rather than from the source, deliberately: the guard
    /// hierarchy in `docs/GUARDS.md` puts compiled inspection above a source scan, and the compiler
    /// knows what the public surface is in a way a regex has to be told. A renamed method does not
    /// escape this; it stops compiling.
    /// </summary>
    private static List<string> UntypedReaders()
    {
        static bool IsRow(Type t) =>
            t.IsGenericType
            && (t.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                || t.GetGenericTypeDefinition() == typeof(List<>)
                || t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            && (t.GetGenericArguments().Any(a => a == typeof(object)) || t.GetGenericArguments().Any(IsRow));

        return typeof(SqliteMemory)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                      | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => IsRow(m.ReturnType)
                     || (Nullable.GetUnderlyingType(m.ReturnType) is { } inner && IsRow(inner)))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// THE RATCHET. Measured at v0.3.8.113, after the approvals slice took it from 50 to 45.
    ///
    /// Lower this number when a slice lands. Do not raise it: a new untyped reader is a new place for
    /// the cipher to be forgotten, and the whole argument for this migration is that the row shape is
    /// where such a rule has nowhere to live.
    /// </summary>
    [Fact]
    public void TheUntypedStoreSurface_OnlyShrinks()
    {
        const int budget = 45;

        var untyped = UntypedReaders();

        Assert.True(untyped.Count <= budget,
            $"the store now exposes {untyped.Count} row-returning public methods; the ratchet is "
          + $"{budget}.\n  " + string.Join("\n  ", untyped)
          + "\nAdding one is adding a place where 'how a row becomes an object' has to be answered "
          + "again — which is how `decision_note` came back encrypted through one approval reader "
          + "and decrypted through another. If a new one is genuinely right, lower nothing and say "
          + "in the same commit why this migration should stop.");

        // AND IT IS NOT VACUOUS. A reflection filter that stopped matching would report zero and
        // pass forever, which is the failure this suite has caught in five separate forms.
        Assert.True(untyped.Count >= 20,
            $"only {untyped.Count} row-returning methods were found. The migration is real but it is "
          + "not that far along — this filter has stopped seeing the surface it measures.");
    }

    /// <summary>
    /// THE APPROVALS SLICE IS TYPED, END TO END — the first one, and the pattern for the rest.
    ///
    /// Asserted through the STORE rather than by reading signatures, so it proves the round trip and
    /// not the declaration: an approval saved as a record comes back as the same record.
    /// </summary>
    [Fact]
    public void AnApproval_RoundTripsAsARecord()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-typed-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            using var memory = new SqliteMemory(Path.Combine(dir, "t.db"));
            var mission = new Mission { Goal = "Restart the media server." };
            memory.SaveMission(mission);

            memory.SaveApprovalRequest(new ApprovalRequest
            {
                MissionId = mission.Id,
                ActionType = ApprovalActionType.ToolUse,
                TargetId = $"{mission.Id}:system_action_execute",
                Title = "Approve the restart?",
                Description = "The mission reached a side-effecting action.",
                RequestedBy = "queen",
                Metadata = new() { ["action"] = "system_action_execute" },
            });

            var read = memory.ApprovalForTarget($"{mission.Id}:system_action_execute", ApprovalActionType.ToolUse);

            Assert.NotNull(read);
            Assert.Equal(ApprovalStatus.Pending, read!.Status);
            Assert.Equal(ApprovalActionType.ToolUse, read.ActionType);
            Assert.Equal("Approve the restart?", read.Title);
            Assert.Equal("queen", read.RequestedBy);
            Assert.Equal("system_action_execute", read.Metadata.GetValueOrDefault("action")?.ToString());
            Assert.Null(read.DecidedAt);
            Assert.Null(read.DecisionNote);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// THE BUG THE SLICE ACTUALLY FIXED, as a test, because it is the reason to do the migration at
    /// all and it would otherwise be invisible in a diff full of signature changes.
    ///
    /// `decision_note` is stored through the field cipher. One of the four readers unprotected it
    /// and three did not, so the same column was plaintext or ciphertext depending on which method
    /// you happened to call. Every reader now goes through one decoder, so the question is asked
    /// once — and this asserts it through the reader that used to get it wrong.
    /// </summary>
    [Fact]
    public void TheDecisionNote_IsDecryptedByEveryReader()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-cipher-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            using var memory = new SqliteMemory(Path.Combine(dir, "t.db"));
            var mission = new Mission { Goal = "Ship it." };
            memory.SaveMission(mission);

            var target = $"{mission.Id}:external_action_execute";
            memory.SaveApprovalRequest(new ApprovalRequest
            {
                MissionId = mission.Id, ActionType = ApprovalActionType.ToolUse, TargetId = target,
                Title = "Approve the send?",
            });

            var pending = memory.ApprovalsForMission(mission.Id).Single();
            const string note = "approved after checking the destination";
            memory.UpdateApprovalStatus(pending.Id, ApprovalStatus.Approved, note);

            // The by-id reader always decrypted. The by-target reader never did — that is the bug.
            Assert.Equal(note, memory.ApprovalById(pending.Id)!.DecisionNote);
            Assert.Equal(note, memory.ApprovalForTarget(target, ApprovalActionType.ToolUse)!.DecisionNote);
            Assert.Equal(note, memory.ApprovalsForMission(mission.Id).Single().DecisionNote);
            Assert.Equal(note, memory.Approvals(ApprovalStatus.Approved).Single(a => a.Id == pending.Id).DecisionNote);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// THE MODULE EDGE STILL CARRIES EVERY KEY THE MODULE READS.
    ///
    /// `ApprovableProjections.FromPatchApproval` lives in `Anthill.Modules.Homelab`, which may
    /// reference the SDK and nothing else of ours — so the core's typed `ApprovalRequest` cannot
    /// cross into it and the API host projects a row at the boundary. That projection is now the one
    /// place a field name has to agree across the boundary, and a rename on either side would empty
    /// the unified approval queue silently: the module would read a missing key as "" and render a
    /// blank card rather than fail.
    /// </summary>
    [Fact]
    public void TheHomelabApprovalProjection_CarriesEveryKeyTheModuleReads()
    {
        var host = Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Api", "Homelab", "ApiHost.Homelab.cs");
        var module = Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Modules",
            "Anthill.Modules.Homelab", "Homelab", "Approvals", "IApprovable.cs");

        Assert.True(File.Exists(host) && File.Exists(module),
            "the approval projection or its consumer has moved; this guard reads nothing.");

        // The value may be `a.Id`, or `EnumExtensions.Value(a.Status)`, or anything else that reads
        // the record — so this matches an assignment whose right-hand side MENTIONS `a.`, not one
        // that starts with it. The narrower pattern was written first and broke on the very next
        // line of the projection it guards, which is defect class 11 committed inside the release
        // that swept for it.
        var projected = Regex.Matches(SourceText.CodeOnly(File.ReadAllText(host)),
                @"\[""(?<key>[a-z_]+)""\]\s*=\s*[^,;\n]*\ba\.")
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var body = SourceText.CodeOnly(File.ReadAllText(module));
        var at = body.IndexOf("FromPatchApproval", StringComparison.Ordinal);
        Assert.True(at >= 0, "FromPatchApproval is gone; this guard is checking nothing.");

        var read = Regex.Matches(SourceText.MemberBody(body, at), @"S\(""(?<key>[a-z_]+)""\)")
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(read.Count >= 5, $"only {read.Count} keys were found in FromPatchApproval.");

        var missing = read.Except(projected).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "the Homelab approval projection does not supply: " + string.Join(", ", missing)
          + ". The module reads a missing key as the empty string, so the unified approval queue "
          + "would render blank cards rather than fail — which is the quietest way for a boundary "
          + "translation to break.");
    }
}
