using Anthill.Core.Agents;
using Anthill.Core.Domain;   // ToolResult
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Sandbox;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.4.0 (ADR-006) — the agent loop: ask, run the tools it asks for, feed the results back, repeat.
///
/// Driven by a SCRIPTED send function rather than a live provider, because the situations worth
/// testing are the ones hardest to provoke on purpose: a model that calls the same tool forever, one
/// that asks for a tool it is not allowed, one that sends malformed arguments, one that never stops.
/// Each of those is a real failure mode of an agent that can edit a repository, and none of them can
/// be summoned reliably by asking a real model nicely.
/// </summary>
public class ToolCallingLoopTests
{
    // ---- fakes -----------------------------------------------------------------------------------

    private sealed class EchoTool : ITool
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "echoes";
        public int Calls { get; private set; }
        public ToolResult Run(IReadOnlyDictionary<string, object?> args)
        {
            Calls++;
            return new ToolResult(Name, true, $"{Name} ran with {args.Count} arg(s)");
        }
    }

    private sealed class FailingTool : ITool
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "always fails";
        public ToolResult Run(IReadOnlyDictionary<string, object?> args) =>
            new(Name, false, "", "disk is on fire");
    }

    private static ToolRegistry RegistryWith(params ITool[] tools)
    {
        var registry = new ToolRegistry(new SqliteMemory(":memory:"));
        foreach (var t in tools) registry.Register(t);
        return registry;
    }

    private static ModelResponse Answer(string text) =>
        new() { Status = ModelCallOutcome.Ok, Content = text };

    private static ModelResponse CallsTool(string name, string argsJson = "{}", string id = "c1") =>
        new()
        {
            Status = ModelCallOutcome.Ok,
            Content = "",
            ToolCalls = new[] { new ModelToolCall(id, name, argsJson) },
        };

    /// <summary>Replays scripted responses in order, repeating the last one forever.</summary>
    private static Func<ModelRequest, ModelResponse> Script(params ModelResponse[] responses)
    {
        var i = 0;
        return _ => responses[Math.Min(i++, responses.Length - 1)];
    }

    private static readonly ModelMessage[] Opening = { new(ModelMessage.User, "do the thing") };

    // ---- the happy path --------------------------------------------------------------------------

    [Fact]
    public void AModelThatAnswersImmediately_Completes_WithoutRunningAnyTool()
    {
        var tool = new EchoTool { Name = "system_info" };
        var result = ToolCallingLoop.Run(Script(Answer("here is your answer")),
            RegistryWith(tool), "researcher", Opening);

        Assert.True(result.Completed);
        Assert.Equal("here is your answer", result.Content);
        Assert.Equal(0, result.ToolCalls);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public void AToolCall_IsExecuted_AndItsOutputFedBack()
    {
        var tool = new EchoTool { Name = "system_info" };
        var result = ToolCallingLoop.Run(
            Script(CallsTool("system_info"), Answer("done")),
            RegistryWith(tool), "researcher", Opening);

        Assert.True(result.Completed);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(1, result.ToolCalls);

        // the tool's real output is in the transcript, tagged to the call that produced it
        var toolMessage = Assert.Single(result.Transcript, m => m.Role == ModelMessage.Tool);
        Assert.Contains("system_info ran", toolMessage.Content);
        Assert.Equal("c1", toolMessage.ToolCallId);
    }

    /// <summary>
    /// The transcript is the artifact. The question asked about an agent run is never "what was the
    /// answer" but "what did it DO", and an answer without the steps cannot be audited or replayed.
    /// </summary>
    [Fact]
    public void TheTranscript_HoldsTheWholeConversation_NotJustTheAnswer()
    {
        var result = ToolCallingLoop.Run(
            Script(CallsTool("system_info"), Answer("all done")),
            RegistryWith(new EchoTool { Name = "system_info" }), "researcher", Opening);

        var roles = result.Transcript.Select(m => m.Role).ToList();
        Assert.Equal(ModelMessage.User, roles[0]);
        Assert.Contains(ModelMessage.Assistant, roles);   // the turn that asked for the tool
        Assert.Contains(ModelMessage.Tool, roles);        // what the tool returned
        Assert.Equal("all done", result.Transcript[^1].Content);
    }

    // ---- failure handling ------------------------------------------------------------------------

    /// <summary>
    /// A failed tool is reported BACK to the model, as text it can act on. A model told
    /// "disk is on fire" picks another route; a model whose call silently vanishes asks again until
    /// the budget is gone.
    /// </summary>
    [Fact]
    public void AFailingTool_IsReportedToTheModel_NotThrown()
    {
        // system_info, because the role must be ALLOWED to run it — the first version of this test
        // named the tool "broken", which researcher may not run at all, so it was quietly asserting
        // on an authorization denial while claiming to test a tool failure. Two different paths that
        // both produce "an ERROR: message in the transcript"; only one of them was under test.
        var result = ToolCallingLoop.Run(
            Script(CallsTool("system_info"), Answer("I will try something else")),
            RegistryWith(new FailingTool { Name = "system_info" }), "researcher", Opening);

        Assert.True(result.Completed);
        var toolMessage = Assert.Single(result.Transcript, m => m.Role == ModelMessage.Tool);
        Assert.Contains("disk is on fire", toolMessage.Content);
    }

    /// <summary>
    /// The other path, now tested on purpose: a tool the role may NOT run. The model is told, in
    /// terms it can act on, rather than the call vanishing — a model that learns "I am not allowed
    /// that" picks a different route, while one whose call disappears asks again until the budget
    /// is gone.
    ///
    /// Note the model asked for a tool that was never offered to it. The projection only OFFERS
    /// permitted tools, but a model can still name anything it likes, so the registry's check
    /// remains the boundary — this proves the loop survives that case rather than trusting the
    /// offer list to constrain the model.
    /// </summary>
    [Fact]
    public void ADeniedTool_IsRefusedAndExplained_NotSilentlyDropped()
    {
        var forbidden = new EchoTool { Name = "apply_patch" };
        var result = ToolCallingLoop.Run(
            Script(CallsTool("apply_patch"), Answer("understood, taking another route")),
            RegistryWith(forbidden), "researcher", Opening);

        Assert.True(result.Completed);
        Assert.Equal(0, forbidden.Calls);                // refused BEFORE the tool ran
        var toolMessage = Assert.Single(result.Transcript, m => m.Role == ModelMessage.Tool);
        Assert.Contains("ERROR", toolMessage.Content);
        Assert.Contains("apply_patch", toolMessage.Content);
    }

    /// <summary>Malformed arguments are the model's mistake, and it can only fix what it is told.</summary>
    [Fact]
    public void MalformedToolArguments_AreExplainedToTheModel()
    {
        var result = ToolCallingLoop.Run(
            Script(CallsTool("system_info", "{not json"), Answer("sorry, retrying")),
            RegistryWith(new EchoTool { Name = "system_info" }), "researcher", Opening);

        var toolMessage = Assert.Single(result.Transcript, m => m.Role == ModelMessage.Tool);
        Assert.Contains("not valid JSON", toolMessage.Content);
    }

    /// <summary>
    /// A provider failure ENDS the loop. Feeding "could not reach the provider" back as an assistant
    /// turn would teach the model that its own last message failed, and models respond by
    /// apologising and retrying — burning the budget on a fault unrelated to the conversation.
    /// </summary>
    [Fact]
    public void AProviderFailure_StopsTheLoop_RatherThanBeingFedBack()
    {
        var result = ToolCallingLoop.Run(
            Script(new ModelResponse { Status = ModelCallOutcome.ConnectError, Content = "ERROR: unreachable" }),
            RegistryWith(new EchoTool { Name = "system_info" }), "researcher", Opening);

        Assert.False(result.Completed);                  // NOT a success
        Assert.Equal(ModelCallOutcome.ConnectError, result.LastStatus);
        Assert.Equal("ERROR: unreachable", result.Content);   // surfaced to the caller...
        // ...but never written into the conversation as though the model had said it
        Assert.DoesNotContain(result.Transcript, m => m.Content.Contains("unreachable"));
    }

    // ---- bounds ----------------------------------------------------------------------------------

    /// <summary>
    /// An agent that can edit a repository must not run forever, and "the model will stop
    /// eventually" is not a budget. A model that only ever calls tools hits the turn cap.
    /// </summary>
    [Fact]
    public void AModelThatNeverStops_IsStoppedByTheTurnBudget()
    {
        var tool = new EchoTool { Name = "system_info" };
        // distinct arguments each time, so it is the TURN cap under test and not repeat detection
        var i = 0;
        var result = ToolCallingLoop.Run(
            _ => CallsTool("system_info", $"{{\"n\":{i++}}}"),
            RegistryWith(tool), "researcher", Opening,
            new LoopBudget(MaxTurns: 3, MaxRepeatedActions: 99));

        Assert.False(result.Completed);
        Assert.Equal("max_turns", result.StopReason);
        Assert.Equal(3, result.Turns);
    }

    /// <summary>
    /// The repeat guard catches the loop that matters: the SAME call made again expecting a
    /// different answer. Arguments are part of the key precisely so that reading two different
    /// files is not mistaken for a repeat.
    /// </summary>
    [Fact]
    public void TheSameCallRepeated_TripsTheRepeatGuard()
    {
        var result = ToolCallingLoop.Run(
            _ => CallsTool("system_info", "{\"path\":\".\"}"),
            RegistryWith(new EchoTool { Name = "system_info" }), "researcher", Opening,
            new LoopBudget(MaxTurns: 20, MaxRepeatedActions: 2));

        Assert.False(result.Completed);
        Assert.Equal("repeated_action", result.StopReason);
    }

    [Fact]
    public void DifferentArguments_AreNotARepeat()
    {
        var files = new[] { "a", "b", "c" };
        var i = 0;
        var result = ToolCallingLoop.Run(
            _ => i < files.Length
                ? CallsTool("read_text_file", $"{{\"path\":\"{files[i++]}\"}}")
                : Answer("read them all"),
            RegistryWith(new EchoTool { Name = "read_text_file" }), "researcher", Opening,
            new LoopBudget(MaxTurns: 10, MaxRepeatedActions: 2));

        Assert.True(result.Completed);
        Assert.Equal(3, result.ToolCalls);
    }

    [Fact]
    public void ACancelledMission_StopsTheLoop()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = ToolCallingLoop.Run(
            Script(CallsTool("system_info")),
            RegistryWith(new EchoTool { Name = "system_info" }), "researcher", Opening,
            new LoopBudget(MaxTurns: 10), cancellationToken: cts.Token);

        Assert.False(result.Completed);
        Assert.Equal("cancelled", result.StopReason);
    }

    // ---- usage -----------------------------------------------------------------------------------

    /// <summary>
    /// Token cost accumulates across the whole loop. A tool-calling run is several model calls, so
    /// reporting only the last one would understate what an agent run actually cost — the number an
    /// operator uses to decide whether to keep running it.
    /// </summary>
    [Fact]
    public void UsageIsSummedAcrossEveryCallInTheLoop()
    {
        var responses = new[]
        {
            CallsTool("system_info") with { Usage = new ModelUsage(10, 5) },
            Answer("done") with { Usage = new ModelUsage(20, 7) },
        };
        var result = ToolCallingLoop.Run(Script(responses),
            RegistryWith(new EchoTool { Name = "system_info" }), "researcher", Opening);

        Assert.Equal(42, result.Usage.TotalTokens);   // 10+5+20+7
    }

    /// <summary>A provider that reports nothing leaves usage UNKNOWN, never zero.</summary>
    [Fact]
    public void ProvidersThatReportNoUsage_LeaveItUnknown()
    {
        var result = ToolCallingLoop.Run(Script(Answer("done")),
            RegistryWith(new EchoTool { Name = "system_info" }), "researcher", Opening);

        Assert.Null(result.Usage.TotalTokens);
    }
}
