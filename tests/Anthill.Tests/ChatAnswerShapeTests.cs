using Anthill.Core.Domain;
using Anthill.Core.Missions;
using Anthill.Core.Orchestration;
using Anthill.Core.Tools;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// WHAT THE OPERATOR READS WHEN THEY ASK THE COLONY A QUESTION. v0.3.8.150.
///
/// EVERY FIXTURE IN THIS FILE IS A REAL MESSAGE, taken from six consecutive chat missions in the
/// operator's own colony immediately after `.148` shipped. That is the point: `.148` was written
/// from a defect the database showed, passed its own suite, and still did not answer the question
/// it was built for — because the suite tested the TOOL and the operator's colony ran the PLAN.
///
/// The six, and what happened:
///
///     "what is the anthill colony?"                                 general        invented an answer
///     "what is forager? and how does it integrate …"                general        CLAIM: … [UNSOURCED]
///     "what is micromound? and why does it integrate into Anthill"  troubleshooting planned a TESTER
///     "what is micromound?"                                         simple_answer  "not in the records"
///     "how do i make Mtn Dew"                                       simple_answer  answered
///
/// Three separate defects, and this file pins one class of fixture against each.
/// </summary>
public class ChatAnswerShapeTests
{
    // ---- 1. a definition is not an inspection, and not a symptom --------------------------------

    /// <summary>
    /// THE NAMED TEST, in the operator's own words, for all four that missed.
    ///
    /// Every one of these names ANTHILL or one of its parts, and naming it is exactly what went
    /// wrong: `ResolveTargets` read a target, a target disqualified `simple_answer`, and the
    /// question was handed to a branch that claims requests by something the colony can DO.
    /// </summary>
    [Theory]
    [InlineData("what is the anthill colony?")]
    [InlineData("what is micromound?")]
    [InlineData("what is micromound? and why does it integrate into Anthill")]
    [InlineData("what is forager? and how does it integrate into the anthill colony?")]
    [InlineData("what are the mission classes")]
    [InlineData("define knowledge scope")]
    public void ADefinitionalQuestionAboutTheColony_IsAnAnswer(string request)
    {
        var specification = MissionIntake.Resolve(request);

        Assert.Equal(MissionSpecification.SimpleAnswerClass, specification.MissionClass);

        // AND IT CARRIES THE CEILING, which is the half that makes the classification safe rather
        // than merely tidier: `MissionAuthorityGate` reads this at dispatch, so no plan built from
        // this specification can patch, write or shell however it was assembled.
        Assert.Equal(MissionAuthority.Observe, specification.Authority);

        // NO REQUIRED EVIDENCE. The `troubleshooting` reading is what demanded a `command_check`
        // receipt for a question about a product feature, and then refused the mission for not
        // having one.
        Assert.Empty(specification.RequiredEvidence);
    }

    /// <summary>
    /// THE ONE THAT MUST STAY OUT, and it is the operator's own message too — from the session
    /// before. "do a self check of the anthill colony, what are the registered roles" names the
    /// colony and asks a question, and it is an AUDIT: it asks what this colony has, which is read
    /// live and cannot come from documentation that ships with every copy.
    ///
    /// The discriminator is the OPENER, not the subject. `DefinitionShape` is anchored at the start
    /// and admits only "what is/are", "what's", "who is", "define", "tell me about" — a bare
    /// "explain" and a leading imperative are both absent on purpose.
    /// </summary>
    [Theory]
    [InlineData("do a self check of the anthill colony, what are the registered roles for it")]
    [InlineData("explain the mission that failed yesterday")]
    [InlineData("audit the anthill repository and tell me what is unimplemented")]
    // THE THREE THE SUITE CAUGHT, and they are the reason the branch carries three exclusions
    // rather than the adjacency test alone. The first is `DeliverableLedgerTests`' and
    // `SystemAuditMissionTests`' shared fixture, which opens definitionally, names the colony, and
    // is an audit in every other respect — documentation cannot say what THIS colony is capable of
    // now, and would answer identically on a colony where the answer is false.
    [InlineData("What is the Anthill colony capable of now? What is good and bad about its "
              + "workflow? Does it hit the proper ants it needs to?")]
    [InlineData("what is the anthill colony running right now")]
    [InlineData("what is the anthill colony, evaluate its weaknesses")]
    public void AQuestionAboutTHISColonysState_IsNotADefinition(string request)
    {
        Assert.NotEqual(MissionSpecification.SimpleAnswerClass, MissionIntake.Resolve(request).MissionClass);
    }

    /// <summary>
    /// THE WORD BOUNDARY, and why the classifier may not reuse `Find`'s matcher.
    ///
    /// `Find` tests containment in both directions, which is right for a LOOKUP — an operator who
    /// knows no topic id still gets something. As a CLASSIFIER it is a disaster: `ants` sits inside
    /// "wants", "constants" and "merchants", so "what is a constant?" would be filed as a question
    /// about the colony's roster and answered from the roster entry.
    /// </summary>
    [Theory]
    [InlineData("what is a constant?")]
    [InlineData("what are merchants")]
    [InlineData("what is a transmission")]
    public void AWordThatMerelyCONTAINSATopic_IsNotTheSubject(string request)
    {
        Assert.False(ColonySelfKnowledge.NamesSubjectOf(request));
    }

    /// <summary>And the names it genuinely does document, in the spellings an operator writes.</summary>
    [Theory]
    [InlineData("what is micromound")]
    [InlineData("what is MICROMOUND?")]
    [InlineData("tell me about forager")]
    [InlineData("what are the mission classes")]   // the topic id is `mission_classes`
    [InlineData("what is knowledge scope")]        // the topic id is `knowledge_scope`
    public void TheNamesItDocuments_AreRecognisedHoweverTheyAreSpelled(string request)
    {
        Assert.True(ColonySelfKnowledge.NamesSubjectOf(request));
    }

    // ---- 2. the answering role can reach the description ---------------------------------------

    /// <summary>
    /// THE DEFECT `.148` LEFT, AND THE REASON IT SURVIVED A GREEN SUITE.
    ///
    /// `.148` granted `colony_self_knowledge` to the researcher and dispatched it there, and every
    /// test it shipped exercised that path. A plain question does not plan a researcher — it plans
    /// `builder -> verifier` — so the tool was registered, granted, dispatched and reaching nobody
    /// who writes an answer. "what is micromound?" came back "micromound remains unaddressed in the
    /// available records", which is the exact sentence `.148` existed to make impossible.
    ///
    /// A RUNTIME TEST CANNOT SEE THIS without standing up a router, so it is a source guard — the
    /// last rung of `docs/GUARDS.md`'s hierarchy, used because the rungs above cannot reach a
    /// prompt. It reads the MEMBER BODY, never a character window (`.92`'s rule), and it resolves
    /// the named helper rather than a literal.
    /// </summary>
    [Fact]
    public void TheBuilderPrompt_CarriesTheShippedSelfDescription()
    {
        var source = SourceText.CodeOnly(
            File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "Ants.cs")));

        var at = source.IndexOf("public sealed class BuilderAnt", StringComparison.Ordinal);
        Assert.True(at > 0, "BuilderAnt is not in Ants.cs — this guard is reading the wrong file.");

        var builder = source[at..];
        var execute = builder.IndexOf("public override AntExecutionResult Execute", StringComparison.Ordinal);
        Assert.True(execute > 0, "BuilderAnt has no Execute — this guard is reading the wrong member.");

        var body = SourceText.MemberBody(builder, execute);

        Assert.Contains("SelfDescriptionBlock(mission.Goal)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONE CORPUS, TWO CONSUMERS — not two copies. The researcher's tool and the builder's context
    /// block must both read `ColonySelfKnowledge`; a second table of the same facts is the defect
    /// `ToolInventory` exists to catch, and it would drift the release after it was written.
    /// </summary>
    [Fact]
    public void TheBuilderReadsTheSameCorpusTheToolDoes()
    {
        var source = SourceText.CodeOnly(
            File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "Ants.cs")));

        var at = source.IndexOf("private static string SelfDescriptionBlock", StringComparison.Ordinal);
        Assert.True(at > 0, "SelfDescriptionBlock is gone; the builder can no longer describe the colony.");

        var body = SourceText.MemberBody(source, at);

        Assert.Contains("ColonySelfKnowledge.Find", body, StringComparison.Ordinal);
        Assert.Contains("ColonySelfKnowledge.NamesSubjectOf", body, StringComparison.Ordinal);
    }

    // ---- 3. the wire format is not the answer ---------------------------------------------------

    private static Mission WithBuilderResult(string result)
    {
        var task = new DomainTask
        {
            Id = "t1",
            Title = "Answer the request",
            AssignedAnt = "builder",
            TaskType = "build_answer",
            Status = TaskStatus.Complete,
            Result = result,
        };
        var mission = new Mission { Id = "m1", Goal = "what is forager?" };
        mission.Tasks.Add(task);
        mission.BestOutputTaskId = ResultAssembler.SelectBestOutputTaskId(mission);
        return mission;
    }

    /// <summary>
    /// THE OPERATOR'S OWN TRANSCRIPT, verbatim, including the empty fourth claim.
    ///
    /// `CLAIM: … [SOURCE: …]` is a MODEL PROTOCOL: the builder is asked for it so each assertion can
    /// be checked against what the mission retrieved. `SourcedAnswer.Render()` has existed since
    /// `.99` to turn it back into prose and nothing on the `user_result` path called it, so the
    /// operator was shown the plumbing — `.147`'s `[d1]` wearing a different handle.
    /// </summary>
    [Fact]
    public void TheClaimWireFormat_DoesNotReachTheOperator()
    {
        var raw =
            "CLAIM: Forager is a role responsible for gathering resources. [UNSOURCED]\n"
          + "CLAIM: Foragers collaborate with scouts to identify resource areas. [UNSOURCED]\n"
          + "CLAIM: No deterministic evidence was recorded. [SOURCE: mission:77057506-3442-423e-b322-74dd44172b11]\n"
          + "CLAIM: [UNSOURCED]";

        var shown = ResultAssembler.ComposeUserResult(WithBuilderResult(raw));

        Assert.DoesNotContain("CLAIM:", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("[UNSOURCED]", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("[SOURCE:", shown, StringComparison.Ordinal);

        // NOTHING IS DROPPED, which is the property that keeps this from being a rewrite: all three
        // real claims survive with their own text, and the unattributed ones are still marked — in
        // words, and more explicitly than the bracket managed.
        Assert.Contains("gathering resources", shown, StringComparison.Ordinal);
        Assert.Contains("collaborate with scouts", shown, StringComparison.Ordinal);
        Assert.Contains("No deterministic evidence", shown, StringComparison.Ordinal);
        Assert.Contains("not attributed", shown, StringComparison.OrdinalIgnoreCase);

        // AND THE EMPTY FOURTH LINE LEAVES. It carried no claim; the parser has always dropped it,
        // and only the raw path ever showed it.
        //
        // COUNTED, NOT SPELLED, and the first cut of this line is worth keeping in the comment: it
        // was `DoesNotContain("CLAIM", OrdinalIgnoreCase)` and it failed on the render's OWN words —
        // "this claim is not attributed to anything the mission retrieved" contains "claim", because
        // a rendered answer talks about claims in English. The wire token is `CLAIM:` WITH THE COLON
        // and that is asserted above; what is left here is arithmetic, so this counts the paragraphs
        // the render produced. Three claims in, three out, and the empty one gone.
        Assert.Equal(3, shown.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    /// <summary>
    /// AND AN ORDINARY ANSWER IS UNTOUCHED, byte for byte. `ComposeUserResult`'s promise is that it
    /// returns the best task's own output; a prose answer must survive it exactly, or this fix has
    /// quietly become a summariser.
    /// </summary>
    [Fact]
    public void AnAnswerThatIsNotClaims_IsReturnedExactly()
    {
        const string prose =
            "To make Mountain Dew, combine 1 cup of water, 3/4 cup of sugar, 1/4 cup of lemon juice\n"
          + "and 1/4 cup of lime juice in a pot.\n\nCLAIMS are not involved here.";

        Assert.Equal(prose, ResultAssembler.ComposeUserResult(WithBuilderResult(prose)));
    }

    // ---- 4. the builder is not offered citations the gate will refuse ---------------------------

    /// <summary>
    /// THE TWO IMPLEMENTATIONS OF ONE RULE, and this is the guard that holds them together.
    ///
    /// The builder's prompt said "cite what you were shown". `CitationIntegrity` said "a recalled
    /// mission that traces to no retrieval is not a source". Both were right and they disagreed, so
    /// a chat question that merely recalled prior missions was handed a list of `mission:<guid>`
    /// urls, wrote claims against them, and had every one refused — the operator paying for the
    /// disagreement instead of a test.
    ///
    /// The offer now runs the gate's own walk. A source guard, because reaching this at runtime
    /// needs an artifact store and a model; what matters is that ONE method is named by both sides.
    /// </summary>
    [Fact]
    public void TheBuilderOffersOnlyCitationsTheGateCanResolve()
    {
        var source = SourceText.CodeOnly(
            File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "Ants.cs")));

        var at = source.IndexOf("private IReadOnlyList<Anthill.SDK.Artifacts.RetrievedSource> CitableSources",
            StringComparison.Ordinal);
        Assert.True(at > 0, "CitableSources is gone; the builder's citation offer is unguarded.");

        var body = SourceText.MemberBody(source, at);

        Assert.Contains("CitationIntegrity.Resolvable", body, StringComparison.Ordinal);
    }
}
