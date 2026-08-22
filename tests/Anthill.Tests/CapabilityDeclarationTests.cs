using System.Reflection;
using Anthill.Core.Agents;
using Anthill.Core.Contracts;
using Anthill.Core.Tools;
// Anthill.SDK.Contracts — where Capability lives — is a global using in this suite.
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// What a role may do is declared in one place, and every name in that declaration means something.
/// v0.3.8.87.
///
/// WHY THIS EXISTS. Two catalogs answered "what does role R require", they disagreed, and the gate
/// that decides ADMISSION read the one nothing enforces.
///
/// <list type="bullet">
/// <item><c>AntExecutionCatalog</c> declares capabilities for all twelve roles and is read by
///   <c>ToolAuthorization.Evaluate</c>, which refuses a dispatch the grant does not cover. ENFORCED.</item>
/// <item><c>ToolCatalog</c> declared them for six, and was read by <c>TaskContract.FromTask</c> →
///   <c>ContractGate.Admit</c> → <c>Planner</c>, which decides whether a planned task may enter the
///   execution queue. NEVER CHECKED AGAINST A GRANT.</item>
/// </list>
///
/// They disagreed about four of the six roles both knew, and the sharpest disagreement is the one
/// that shows why nothing noticed: <c>ToolCatalog</c> required <c>repo.write.sandbox</c> for the
/// builder, and <c>CapabilityGrant</c> is written never to grant it — in a comment that names it.
/// A requirement nothing could satisfy, declared beside a check nothing ran.
///
/// <c>ToolCatalog.CanRun</c> — the pre-execution permission check — had no production caller in its
/// whole life. Its one caller was a test that built the descriptor AND the grant set itself, which
/// is the failure <c>FailureClassNames</c> already recorded in this repository, in those words: *no
/// test anywhere ran a value from a real producer into a real consumer.*
///
/// Every assertion below runs a real producer into a real consumer, and the last one keeps the
/// second declaration from growing back.
/// </summary>
public class CapabilityDeclarationTests
{
    /// <summary>Every <c>public const string</c> on <see cref="Capability"/>, by name.</summary>
    private static Dictionary<string, string> DeclaredCapabilities() =>
        typeof(Capability)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    /// <summary>Every capability any role contract requires, deduplicated.</summary>
    private static HashSet<string> RequiredByAnyRole() =>
        AntExecutionCatalog.Contracts.Values
            .SelectMany(c => c.RequiredCapabilities)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A contract may not require a capability that is not in the vocabulary.
    ///
    /// Weaker than it sounds only if you have not seen the failure mode: a typo'd capability string
    /// is required by the contract, never granted by anything, and refuses the role at dispatch with
    /// a message about a capability that does not exist.
    /// </summary>
    [Fact]
    public void EveryCapabilityARoleRequires_IsOneTheVocabularyDeclares()
    {
        var vocabulary = DeclaredCapabilities().Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = AntExecutionCatalog.Contracts
            .SelectMany(kv => kv.Value.RequiredCapabilities.Select(c => (Role: kv.Key, Capability: c)))
            .Where(x => !vocabulary.Contains(x.Capability))
            .Select(x => $"{x.Role} requires \"{x.Capability}\"")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0,
            "these role contracts require capability names the vocabulary does not declare:\n  "
          + string.Join("\n  ", unknown)
          + "\nA capability nothing declares is granted by nothing, so the role is refused at "
          + "dispatch for a reason no operator can act on.");
    }

    /// <summary>
    /// FULL REALLY IS THE MOST PERMISSIVE GRANT — the property the register below rests on, and one
    /// nothing checked.
    ///
    /// "Every capability a role requires can be granted by some colony" is already proved, by
    /// <c>StageBConsequentialTests.AFullyEquippedColony_SatisfiesEveryContractsRequirements</c>, and
    /// restating it here would be this release's own defect with a new file name. What that test
    /// uses is <see cref="CapabilityGrant.Resolve"/> over a fully-equipped tool set; what the
    /// register and the API projection use is <see cref="CapabilityGrant.Full"/>, a hand-written
    /// list of six.
    ///
    /// Those are two answers to "what can be granted", and nothing compared them. A capability added
    /// to `Resolve` and not to `Full` would make `Full` — the list named for being permissive —
    /// quietly narrower than a real colony, and would make an orphan below look correctly withheld.
    /// </summary>
    [Fact]
    public void TheFullGrant_CoversEverythingAnEquippedColonyResolves()
    {
        var equipped = CapabilityGrant.Resolve(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "system_info", "read_text_file", "list_directory", "search_workspace",
                "repository_index", "run_allowlisted_check", "web_search",
            },
            modelAvailable: true, webSearchEnabled: true);

        var missingFromFull = equipped
            .Where(c => !CapabilityGrant.Full.Contains(c))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.True(missingFromFull.Count == 0,
            "a fully-equipped colony resolves capabilities that CapabilityGrant.Full does not list: "
          + string.Join(", ", missingFromFull)
          + ". `Full` is documented as the permissive answer and is what the API projection and the "
          + "ungranted register are read against, so a capability it omits reads as impossible "
          + "while a real run grants it.");

        Assert.True(equipped.Count > 0, "an equipped colony resolved no capabilities at all.");
    }

    /// <summary>
    /// EVERY DECLARED CAPABILITY MEANS SOMETHING: granted, required, or withheld on the record.
    ///
    /// This is v0.3.8.86's finding one vocabulary over. There, two event constants nothing published
    /// were near-misses of real event names, so a subscriber filtering on either matched nothing
    /// forever. Here the equivalent is a permission name that reads like a real grant and is issued
    /// by nobody — which looks exactly like a capability that exists and is simply not enabled.
    ///
    /// The third arm is deliberate and is the reason this is not just a phantom hunt. Seven of the
    /// fourteen names are withheld ON PURPOSE — <c>repo.patch.apply</c> most importantly, because
    /// applying is the approval pipeline's alone. Deleting those would remove the distinction that
    /// makes <c>repo.patch.propose</c> meaningful. So they are named in
    /// <see cref="CapabilityGrant.DeliberatelyUngranted"/> with a reason each, and the difference
    /// between "withheld" and "forgotten" becomes something the tree records rather than something a
    /// reader infers.
    /// </summary>
    [Fact]
    public void EveryDeclaredCapability_IsGrantedRequiredOrWithheldOnTheRecord()
    {
        var required = RequiredByAnyRole();
        var grantable = CapabilityGrant.Full;
        var withheld = CapabilityGrant.DeliberatelyUngranted;

        var orphaned = DeclaredCapabilities()
            .Where(kv => !required.Contains(kv.Value)
                      && !grantable.Contains(kv.Value)
                      && !withheld.ContainsKey(kv.Value))
            .Select(kv => $"{kv.Key} = \"{kv.Value}\"")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphaned.Count == 0,
            "these capabilities are declared, granted by nothing, required by nobody, and not "
          + "recorded as deliberately withheld:\n  " + string.Join("\n  ", orphaned)
          + "\nAdd the name to CapabilityGrant.DeliberatelyUngranted with the reason, or remove it. "
          + "A permission name that reaches nobody is indistinguishable from one that is merely "
          + "switched off, and an operator cannot tell which they are looking at.");

        // The other direction: a name recorded as withheld that a colony can actually grant is a
        // register that has gone stale, and a stale register is worse than none — it states, in
        // writing, that something is impossible while the runtime does it.
        var contradicted = withheld.Keys
            .Where(grantable.Contains)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(contradicted.Count == 0,
            "these capabilities are recorded as deliberately never granted and CapabilityGrant.Full "
          + "grants them: " + string.Join(", ", contradicted)
          + ". Remove the register entry and read its reason first — it was written because someone "
          + "decided this capability must not reach a mission agent.");
    }

    /// <summary>
    /// THE ADMISSION GATE AND THE DISPATCH GATE READ THE SAME BOOK.
    ///
    /// The defect this release fixes, stated as the property that would have caught it. Both ends are
    /// real: <c>TaskContract.FromTask</c> is what <c>ContractGate.Admit</c> actually calls, and
    /// <c>AntExecutionCatalog</c> is what <c>ToolAuthorization.Evaluate</c> actually reads.
    /// </summary>
    [Fact]
    public void TheAdmissionProjection_DeclaresWhatTheDispatchGateEnforces()
    {
        var disagreements = new List<string>();

        foreach (var (role, contract) in AntExecutionCatalog.Contracts)
        {
            var projected = TaskContract.FromTask(new DomainTask
            {
                Title = $"Probe {role}", Description = "Capability projection probe.",
                AssignedAnt = role, TaskType = contract.SupportedTaskTypes.FirstOrDefault() ?? "research",
            });

            var enforced = contract.RequiredCapabilities.OrderBy(c => c, StringComparer.Ordinal).ToList();
            var admitted = projected.RequiredCapabilities.OrderBy(c => c, StringComparer.Ordinal).ToList();

            if (!enforced.SequenceEqual(admitted, StringComparer.OrdinalIgnoreCase))
                disagreements.Add(
                    $"{role}: dispatch enforces [{string.Join(", ", enforced)}] and admission "
                  + $"projects [{string.Join(", ", admitted)}]");
        }

        Assert.True(disagreements.Count == 0,
            "the admission gate and the dispatch gate disagree about what these roles require:\n  "
          + string.Join("\n  ", disagreements)
          + "\nOne of them decides whether a task may enter the queue and the other decides whether "
          + "it may call a tool. Two books mean a task can be admitted on one declaration and "
          + "refused on the other, and the mismatch surfaces as a dispatch failure in a mission that "
          + "was already running.");
    }

    /// <summary>
    /// A ROLE THAT REQUIRES NOTHING CAN SAY SO — and an unknown role still cannot.
    ///
    /// The guard split, as a regression test. <c>Validate</c> used to reject any empty capability
    /// list, which is right for "we do not know what this ant needs" and wrong for the archivist,
    /// whose contract declares <c>S()</c> because the Queen hands it a finished mission and it
    /// touches nothing else. The projection escaped by declaring <c>model.invoke</c> for every role
    /// the old catalog did not list — including five that hold no ModelRouter at all.
    ///
    /// Both halves are asserted together because the split is only safe if the strict half stayed
    /// strict. If the second assertion ever goes green on an unknown ant, the guard was not split,
    /// it was removed.
    /// </summary>
    [Fact]
    public void ARoleRequiringNothing_IsAdmissible_AndAnUnknownRoleIsStillRefused()
    {
        var zeroCapabilityRoles = AntExecutionCatalog.Contracts
            .Where(kv => kv.Value.RequiredCapabilities.Count == 0)
            .Select(kv => kv.Key)
            .ToList();

        Assert.True(zeroCapabilityRoles.Count > 0,
            "no role contract declares zero capabilities any more, so this guard is asserting "
          + "nothing. The archivist was the case it was written for; if its requirements changed, "
          + "decide whether the split in TaskContract.Validate is still earning its place.");

        foreach (var role in zeroCapabilityRoles)
        {
            var contract = TaskContract.FromTask(new DomainTask
            {
                Title = $"Probe {role}", Description = "Zero-capability admission probe.",
                AssignedAnt = role, TaskType = "memory_consolidation",
            });

            Assert.True(contract.CapabilitiesDeclaredByContract,
                $"'{role}' has a contract and the projection did not mark its capabilities as "
              + "contract-declared, so an honest empty declaration is about to be read as an unknown "
              + "ant.");

            Assert.DoesNotContain(contract.Validate(), e => e.Contains("permission-checked"));
        }

        // And the half that must not move. An ant no contract describes is still refused, and still
        // refused for the reason it always was.
        var unknown = TaskContract.FromTask(new DomainTask
        {
            Title = "Probe", Description = "Unknown ant.", AssignedAnt = "mystery", TaskType = "research",
        });

        Assert.False(unknown.CapabilitiesDeclaredByContract);
        Assert.Contains(unknown.Validate(), e => e.Contains("permission-checked"));
    }

    /// <summary>
    /// CAPABILITIES ARE DECLARED IN EXACTLY ONE PLACE, and this is what stops the second book coming
    /// back.
    ///
    /// A source guard rather than a behavioural one, because the behaviour it protects is the absence
    /// of something: <c>TheAdmissionProjection_DeclaresWhatTheDispatchGateEnforces</c> would still
    /// pass if someone reintroduced a duplicate list that happened to agree today. What fails then is
    /// the next edit to one of the two.
    ///
    /// Reads <c>SourceText.CodeOnly</c> so the explanatory prose in ToolVocabulary.cs — which names
    /// several capabilities while describing why they left — does not match itself.
    /// </summary>
    [Fact]
    public void OnlyTheRoleContracts_DeclareRoleCapabilities()
    {
        var vocabulary = Path.Combine(SourceText.RepoRoot(), "src", "Anthill.SDK", "Contracts", "ToolVocabulary.cs");
        Assert.True(File.Exists(vocabulary),
            "ToolVocabulary.cs has moved; this guard no longer reads the file it names and would "
          + "pass over nothing.");

        var code = SourceText.CodeOnly(File.ReadAllText(vocabulary));

        // The vocabulary itself must still be here — a guard that passed because the file emptied
        // would be the vacuity failure this suite keeps finding.
        Assert.Contains("class Capability", code, StringComparison.Ordinal);

        foreach (var reintroduced in new[] { "RequiredCapabilities", "SideEffectClass", "RiskClass", "class ToolCatalog" })
            Assert.False(code.Contains(reintroduced, StringComparison.Ordinal),
                $"ToolVocabulary.cs declares '{reintroduced}' again. That is the second book: this "
              + "file is the shared VOCABULARY — what a capability is called — and "
              + "AntExecutionCatalog is the declaration of which role requires which. "
              + $"They were split in {Anthill.SDK.Contracts.ToolVocabularyHistory.SecondCatalogRemovedIn}; "
              + "the note at the bottom of that file lists what the two disagreed about while both "
              + "existed, and it is worth reading before putting one back.");
    }

    /// <summary>
    /// Neither the vocabulary nor the contract set may be empty. Every assertion above ranges over
    /// one of the two, and a reflection change or a catalog rename would otherwise leave the whole
    /// file green over nothing — which is how the divergence it describes survived as long as it did.
    /// </summary>
    [Fact]
    public void TheSweep_SeesBothTheVocabularyAndTheContracts()
    {
        var declared = DeclaredCapabilities();
        Assert.True(declared.Count >= 10,
            $"only {declared.Count} capability constants were found by reflection; the vocabulary "
          + "has more than that, so something about how they are declared has changed.");

        Assert.True(AntExecutionCatalog.Contracts.Count >= 12,
            $"only {AntExecutionCatalog.Contracts.Count} role contracts were found; the colony has "
          + "twelve roles and every assertion here ranges over this set.");

        Assert.True(RequiredByAnyRole().Count >= 5,
            "fewer than five distinct capabilities are required by any role, which would mean the "
          + "contracts stopped declaring them rather than that the colony got simpler.");
    }
}
