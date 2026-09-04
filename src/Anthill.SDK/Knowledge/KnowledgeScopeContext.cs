namespace Anthill.SDK.Knowledge;

/// <summary>
/// The knowledge scope in force for the current asynchronous flow.
///
/// WHY AMBIENT, when this repository's standing preference — stated in ADR-002 and honoured by
/// <c>MissionContext</c> — is to pass context explicitly:
///
/// <c>ITool.Run</c> receives arguments and nothing else. The registry knows the mission id, the task
/// id and the role, and deliberately does not hand them to the tool. So a knowledge tool has exactly
/// two ways to learn which project it may read from: an argument, or an ambient scope.
///
/// AN ARGUMENT IS NOT AN OPTION, and this is the security argument for the whole file. Tool
/// arguments are chosen by a MODEL. A <c>project_id</c> parameter would make the scope of a
/// knowledge query something the model selects, which means a model that has read a project id in
/// one context — from a document, from a previous answer, from a hallucination that happens to be
/// right — could read another project's knowledge base. Rule 12 would then be enforced by the
/// model's discretion, which is not enforcement.
///
/// So the scope is ambient, set by the core at mission intake, and there is no supported way for a
/// tool call to widen it. The same reasoning <c>MissionWorkspaceScope</c> uses for workspace roots,
/// for the same reason: tools are startup singletons and scopes are per-mission.
///
/// IT ONLY EVER NARROWS. <see cref="Enter"/> nests and restores on dispose; nothing here can widen
/// an existing scope, and the default when nobody has entered one is
/// <see cref="KnowledgeScope.Unresolved"/> — which retrieves nothing rather than everything.
/// </summary>
public static class KnowledgeScopeContext
{
    private static readonly AsyncLocal<KnowledgeScope?> Ambient = new();

    /// <summary>
    /// The scope in force, or <see cref="KnowledgeScope.Unresolved"/> when none has been entered.
    ///
    /// Unresolved is a REFUSAL, not a wildcard. A caller outside any mission — a background task, a
    /// stray thread — gets nothing, which is the only safe default for a value whose job is to bound
    /// what may be read.
    /// </summary>
    public static KnowledgeScope Current => Ambient.Value ?? KnowledgeScope.Unresolved;

    /// <summary>Whether a usable scope is in force right now.</summary>
    public static bool HasScope => Current.IsQueryable;

    /// <summary>
    /// Enter a scope for the duration of the returned handle. Nests; restores the previous value on
    /// dispose, including when an exception unwinds through it.
    /// </summary>
    public static IDisposable Enter(KnowledgeScope? scope)
    {
        var previous = Ambient.Value;
        Ambient.Value = scope;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly KnowledgeScope? _previous;
        private bool _done;

        public Restore(KnowledgeScope? previous) => _previous = previous;

        public void Dispose()
        {
            // Idempotent: a using-block that also gets disposed by a finally must not restore twice
            // and resurrect a scope that a nested Enter had already unwound.
            if (_done) return;
            _done = true;
            Ambient.Value = _previous;
        }
    }
}
