namespace Anthill.SDK.Reasoning;

/// <summary>
/// A reasoning provider that is an AUTONOMOUS CODING AGENT — it edits files, runs commands and acts
/// on the operator's machine rather than only answering. v0.3.8.57.
///
/// WHY THIS EXISTS. Every provider satisfies <see cref="IReasoningProvider"/>, so from the router's
/// side an agent CLI and a chat model are interchangeable: ask, receive text. They are not
/// interchangeable in what they DO. Asking Ollama a question produces an answer; asking Claude Code
/// the same question can produce an answer AND a modified working tree, with no task, no plan, no
/// tester, no soldier and no verifier anywhere in the sequence.
///
/// That difference had no name in the type system, so nothing could enforce it. `conversation` is a
/// route key like any other, and pointing it at an installed agent made the CHAT BOX a direct line to
/// a coding agent — the colony reduced to a text field in front of someone else's tool. v0.3.8.53
/// built containment for the changes that lane produced (the `direct_change` artifact, explicitly
/// unverified, never feeding positive memory), which was the right response to the symptom and left
/// the shape intact.
///
/// A MARKER, DECLARED BY THE PROVIDER. The alternative was a list of agent ids in the core, and the
/// core may not name a provider implementation — a rule this file exists to respect rather than
/// route around. More usefully, a provider that knows what it is cannot fall out of step with a list
/// that someone else maintains: a new agent CLI is contained the moment it is written, not the
/// moment somebody remembers to add it.
///
/// NOT A JUDGEMENT ABOUT QUALITY. An agent CLI is the most capable thing the colony can dispatch,
/// and the coder role uses one deliberately. The rule is about WHO DISPATCHES IT: the colony sends
/// work to an agent as a tool, inside a mission that plans, reviews, tests and verifies it. The
/// operator does not send work to an agent through a chat box that happens to be wired to one.
/// </summary>
public interface IAutonomousCodingAgent
{
    /// <summary>
    /// What to call this agent when explaining a refusal to an operator. The provider's own name for
    /// itself, so the message says "claude" rather than a route key nobody recognises.
    /// </summary>
    string AgentDisplayName { get; }
}
