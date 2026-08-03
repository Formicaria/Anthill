using System.Text.Json;
using Anthill.Core.Models;
using Anthill.Core.Sandbox;
using Anthill.Core.Tools;

namespace Anthill.Core.Agents;

/// <summary>What a completed tool-calling conversation produced.</summary>
public sealed record ToolLoopResult(
    string Content,
    IReadOnlyList<ModelMessage> Transcript,
    int Turns,
    int ToolCalls,
    string StopReason,
    ModelCallOutcome LastStatus,
    ModelUsage Usage)
{
    /// <summary>
    /// Completed means the MODEL finished with an answer, not merely that nothing crashed. A loop
    /// stopped by its turn budget has a transcript and no conclusion, and calling that success is
    /// how a half-finished agent run gets recorded as a completed task.
    /// </summary>
    public bool Completed => StopReason == "completed" && LastStatus == ModelCallOutcome.Ok;
}

/// <summary>
/// v3.4.0 (ADR-003) — the agent loop: ask, run the tools it asks for, feed the results back, repeat.
///
/// This is the piece that turns "the colony CAN call tools" into "the colony does work". Everything
/// under it already existed: the provider seam carries tool schemas and returns typed tool calls,
/// the registry executes them under authorization, and <see cref="BoundedAgentLoop"/> supplies the
/// budget and stop reasons. What was missing is the conversation that joins them.
///
/// BOUNDED BY CONSTRUCTION. It runs inside BoundedAgentLoop, so turns, tool calls, wall-clock and
/// repeated identical actions are all capped, and cancellation is the mission's existing ambient
/// token. An agent that can edit a repository must not be able to run forever, and "the model will
/// stop eventually" is not a budget.
///
/// The transcript is the artifact. Every message — including each tool's actual output — is kept,
/// because the question an operator asks about an agent run is never "what was the answer" but
/// "what did it DO", and an answer without the steps cannot be audited or replayed.
/// </summary>
public static class ToolCallingLoop
{
    /// <summary>
    /// Run a tool-calling conversation for <paramref name="role"/>.
    ///
    /// The offered toolset is resolved ONCE, before the first call: it is a property of the role,
    /// not of the turn, and re-resolving each turn would let the tool list shift mid-conversation
    /// for no reason the model could understand.
    /// </summary>
    public static ToolLoopResult Run(
        ModelRouter router,
        ToolRegistry registry,
        string role,
        IReadOnlyList<ModelMessage> opening,
        LoopBudget? budget = null,
        string? missionId = null,
        string? taskId = null,
        string? model = null,
        CancellationToken cancellationToken = default) =>
        Run(request => router.SendTyped(role, request, missionId, taskId, role),
            registry, role, opening, budget, missionId, taskId, model, cancellationToken);

    /// <summary>
    /// The same loop over an arbitrary send function.
    ///
    /// This overload exists because the loop's own logic — when to stop, what to feed back, what
    /// counts as a repeat — is the part most worth testing and the part hardest to reach through a
    /// live provider. A scripted <paramref name="send"/> drives conversations that are difficult to
    /// provoke on purpose: a model that calls the same tool twice, one that asks for a tool it may
    /// not use, one that never stops asking.
    ///
    /// It is a narrow seam rather than an invented abstraction: the loop genuinely needs nothing
    /// from the router except "send this, get that", and the router overload above is the only
    /// production caller.
    /// </summary>
    public static ToolLoopResult Run(
        Func<ModelRequest, ModelResponse> send,
        ToolRegistry registry,
        string role,
        IReadOnlyList<ModelMessage> opening,
        LoopBudget? budget = null,
        string? missionId = null,
        string? taskId = null,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ModelMessage>(opening ?? Array.Empty<ModelMessage>());
        var tools = ToolSchemaProjection.For(registry, role);

        var content = "";
        var lastStatus = ModelCallOutcome.Empty;
        var promptTokens = 0;
        var completionTokens = 0;
        var sawUsage = false;

        var outcome = BoundedAgentLoop.Run(budget ?? new LoopBudget(), turn =>
        {
            // Messages are COPIED per turn: the request is a snapshot of the conversation so far,
            // and handing the live list over would let a later turn's appends mutate a request that
            // has already been sent.
            // An explicit model overrides the role's route. Per-agent model assignment is a
            // request-level decision — "run this agent on the coder model" is a thing an operator
            // wants to say without reconfiguring routing for every mission.
            var response = send(new ModelRequest { Messages = messages.ToList(), Tools = tools, Model = model });

            lastStatus = response.Status;
            if (response.Usage.PromptTokens is { } p) { promptTokens += p; sawUsage = true; }
            if (response.Usage.CompletionTokens is { } c) { completionTokens += c; sawUsage = true; }

            // A provider failure ENDS the loop rather than being fed back as if the model had said
            // it. Appending "ERROR: could not reach the provider" as an assistant turn would teach
            // the model that its own last message failed, and models respond to that by apologising
            // and retrying — burning the remaining budget on a fault that has nothing to do with
            // the conversation.
            if (!response.Ok)
            {
                content = response.Content;
                return new LoopStep(Done: true, ActionKey: $"provider_fault:{response.Status.Name()}",
                    ToolCallsUsed: 0, Note: response.Status.Name());
            }

            // No tool calls means the model answered. That is the ONLY completion condition here.
            if (response.ToolCalls.Count == 0)
            {
                content = response.Content;
                messages.Add(new ModelMessage(ModelMessage.Assistant, content));
                return new LoopStep(Done: true, ActionKey: "final_answer", ToolCallsUsed: 0);
            }

            // The assistant's turn carries the CALLS IT MADE, not just its (usually empty) prose.
            // Recording only the text produced a conversation in which tool results answered
            // requests that were not present, and a model replayed that transcript cannot see it
            // already called the tool — so it calls again, forever, until the repeat guard fires.
            messages.Add(new ModelMessage(ModelMessage.Assistant, response.Content)
            {
                ToolCalls = response.ToolCalls,
            });

            foreach (var call in response.ToolCalls)
            {
                var output = Execute(registry, role, call, missionId, taskId);
                messages.Add(new ModelMessage(ModelMessage.Tool, output) { ToolCallId = call.Id });
            }

            // The action key is the tool names and their arguments together. Names alone would
            // treat "read file A" and "read file B" as a repeat and stop a legitimate sweep;
            // including arguments catches the loop that actually matters — the same call, made
            // again, expecting a different answer.
            var actionKey = string.Join("|", response.ToolCalls.Select(c => $"{c.Name}({c.ArgumentsJson})"));
            return new LoopStep(Done: false, ActionKey: actionKey, ToolCallsUsed: response.ToolCalls.Count);
        }, cancellationToken);

        return new ToolLoopResult(
            Content: content,
            Transcript: messages,
            Turns: outcome.Turns,
            ToolCalls: outcome.ToolCalls,
            StopReason: outcome.StopReason,
            LastStatus: lastStatus,
            Usage: sawUsage ? new ModelUsage(promptTokens, completionTokens) : ModelUsage.Unknown);
    }

    /// <summary>
    /// Run one tool call and render its result for the model.
    ///
    /// A refused or failed tool is reported back as TEXT the model can act on — not thrown, and not
    /// silently dropped. The model asked for something it could not have, and the useful thing is
    /// telling it so: a model that learns "apply_patch is denied to me" picks a different route,
    /// while a model whose call vanishes simply asks again until the budget is gone.
    ///
    /// The registry still enforces authorization; this only decides how the refusal is worded.
    /// </summary>
    private static string Execute(ToolRegistry registry, string role, ModelToolCall call,
        string? missionId, string? taskId)
    {
        Dictionary<string, object?> args;
        try
        {
            args = string.IsNullOrWhiteSpace(call.ArgumentsJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(call.ArgumentsJson)
                  ?? new Dictionary<string, object?>();
        }
        catch (JsonException error)
        {
            // Malformed arguments are the model's mistake and it can correct them — but only if it
            // is told, which is why this is a message rather than an exception.
            return $"ERROR: arguments for '{call.Name}' were not valid JSON ({error.Message}). "
                 + "Send the arguments as a JSON object matching the tool's schema.";
        }

        var result = registry.RunTool(call.Name, missionId, taskId, role, args);
        return result.Success
            ? result.Output
            : $"ERROR: {call.Name} failed — {result.Error ?? "no reason given"}\n{Guidance(result)}";
    }

    /// <summary>
    /// Turn a typed failure into the one sentence that changes what the model does next.
    ///
    /// The classes exist so this decision stops being guesswork. Told only "the tool failed", a
    /// model has one move available — call it again — and it will, until the budget is gone. Told
    /// that it may not use the tool, it routes around; told the arguments were wrong, it fixes them;
    /// told the failure was transient, retrying is the correct move rather than a waste.
    ///
    /// The wording is instruction, not description, because a model reading a status word decides
    /// for itself what it implies, and "authorization_denied" reads to most models as something to
    /// apologise for and then attempt again.
    /// </summary>
    internal static string Guidance(Domain.ToolResult result) => result.Failure switch
    {
        Contracts.FailureClass.AuthorizationFailure =>
            "You are not permitted to use this tool. Do not call it again — achieve the goal another "
          + "way, or say what you would need.",
        Contracts.FailureClass.ValidationFailure =>
            "The call itself was wrong, not the tool. Correct the tool name or the arguments against "
          + "the schema and try once more.",
        _ when result.Retryable =>
            "This failure is transient. Calling it again with the same arguments may succeed.",
        _ =>
            "This failure will repeat if you call it the same way. Change approach or report that "
          + "you could not complete the step.",
    };
}
