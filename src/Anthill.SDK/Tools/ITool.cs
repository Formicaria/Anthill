namespace Anthill.SDK.Tools;

// v3.8.10 — the tool contract, moved once ToolResult could follow it. Implementations belong in
// modules; registration, authorization and dispatch stay in the core, because deciding WHICH tool
// runs and whether it is permitted is coordination.


/// <summary>A read-mostly capability the ants can invoke. Every tool fails closed when its config gate is off.</summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    ToolResult Run(IReadOnlyDictionary<string, object?> args);

    /// <summary>
    /// v3.4.0 (ADR-006): the tool's arguments as a JSON Schema object, for offering it to a model.
    ///
    /// A DEFAULT member, so no existing tool breaks and none is forced to describe itself before it
    /// has anything to describe. The default states an object with no declared properties — exactly
    /// right for a tool that takes none, and for one that takes arguments it has not declared yet it
    /// degrades to "callable with nothing", which fails visibly AT THE TOOL rather than silently
    /// producing plausible-looking wrong arguments.
    ///
    /// Deliberately a schema STRING and not a typed object graph: it is handed to the provider
    /// verbatim, every provider wants JSON Schema, and modelling JSON Schema in C# would mean
    /// maintaining a translation layer for a format nobody disagrees about.
    /// </summary>
    string ParametersJson => """{"type":"object","properties":{}}""";
}
