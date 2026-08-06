namespace Anthill.SDK.Modules;

/// <summary>
/// A unit of colony capability that lives outside the core.
///
/// Homelab, the reasoning providers, shell/git/filesystem tooling, vision, analytics — everything
/// that is not scheduling, memory or coordination. The core must never name a module type; a module
/// reaches the colony only by implementing this and being composed in at startup.
///
/// The interface is small on purpose. A module registration surface that grows hooks — pre-mission,
/// post-task, on-plan — turns into a second scheduler operating by callback, which is the exact
/// architecture this refactor exists to undo. Modules contribute capability and observe events.
/// They do not steer the colony.
/// </summary>
public interface IAnthillModule
{
    /// <summary>
    /// Stable identifier, used in configuration and in the event metadata a module publishes.
    /// Lowercase, dotted: "homelab", "reasoning.ollama", "tools.shell".
    /// </summary>
    string Name { get; }

    string Version { get; }

    /// <summary>
    /// Contribute capability. Called once at startup, before any mission runs.
    ///
    /// Registration must be cheap and must not perform I/O: a module that dials a remote host here
    /// makes an unreachable server into a colony that will not boot. Connect lazily on first use,
    /// and report the failure as an event.
    /// </summary>
    void Register(IModuleContext context);
}
