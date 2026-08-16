using System.Text.Json.Serialization;
using Anthill.Core.Tools;

namespace Anthill.Core.Configuration;

/// <summary>
/// One check an operator declares for their workspace. v0.3.8.73.
///
/// The same shape as <see cref="CheckDefinition"/> and deliberately not the same type: this one
/// crosses the JSON boundary and can therefore arrive malformed, half-filled or hostile, and
/// <see cref="Validate"/> is where that stops. A configuration record that could be used directly as
/// a runtime definition would make "did anyone check this" a question about the call site.
/// </summary>
public sealed class ConfiguredCheck
{
    /// <summary>Stable id the tester names and the runner resolves.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>The executable. NOT a shell line — arguments are separate and neither is parsed
    /// from anything a model wrote.</summary>
    [JsonPropertyName("command")] public string Command { get; set; } = "";

    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";

    [JsonPropertyName("timeout_seconds")] public int TimeoutSeconds { get; set; } = 600;

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonPropertyName("description")] public string Description { get; set; } = "";
}

/// <summary>
/// Turning declared checks into runnable ones, with every refusal named. v0.3.8.73.
///
/// A PURE FUNCTION over the parsed configuration, for the reason <see cref="ConfigSchema"/> and
/// <see cref="RosterProfiles"/> are: it is exhaustively testable without a filesystem, and it cannot
/// be called before its inputs exist. It returns accepted checks AND the problems, rather than
/// throwing — a single bad entry must not cost an operator every other check they declared, and a
/// silently dropped one is worse than a loud refusal.
/// </summary>
public static class WorkspaceCheckConfig
{
    public sealed record Result(
        IReadOnlyList<CheckDefinition> Checks,
        IReadOnlyList<string> Problems);

    /// <summary>Timeout bounds. A zero or negative timeout is a check that cannot pass; an unbounded
    /// one is a hung mission the operator reads as a slow one.</summary>
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3600;

    public static Result Resolve(IEnumerable<ConfiguredCheck>? declared)
    {
        var checks = new List<CheckDefinition>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (entry, index) in (declared ?? Array.Empty<ConfiguredCheck>()).Select((e, i) => (e, i)))
        {
            if (entry is null) { problems.Add($"workspace_checks[{index}] is null"); continue; }

            var id = (entry.Id ?? "").Trim();
            var command = (entry.Command ?? "").Trim();

            if (id.Length == 0) { problems.Add($"workspace_checks[{index}] has no id"); continue; }

            // An id the tester cannot name is an id the tester cannot run. The selection matches ids
            // against task text, so an id containing whitespace or quotes would match unpredictably
            // and is refused rather than normalised — silently renaming an operator's id would make
            // the configuration and the log disagree.
            if (id.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '`'))
            {
                problems.Add($"workspace_checks[{index}] id '{id}' contains whitespace or quotes");
                continue;
            }

            if (command.Length == 0)
            {
                problems.Add($"workspace_checks[{index}] ('{id}') has no command");
                continue;
            }

            // COLLISION WITH A BUILT-IN IS REFUSED, not silently preferred either way. `dotnet_build`
            // means one thing across this repository — the auto-apply verify path, the graduation
            // record and every changelog entry name it — and letting configuration redefine the name
            // while keeping the meaning is how a report comes to describe a check that did not run.
            // An operator wanting different behaviour gives it a different id.
            if (CheckCatalog.IsBuiltIn(id))
            {
                problems.Add(
                    $"workspace_checks[{index}] id '{id}' collides with a built-in check. Built-in "
                  + "ids have a fixed meaning across the colony's records; choose another id.");
                continue;
            }

            if (!seen.Add(id))
            {
                problems.Add($"workspace_checks[{index}] repeats id '{id}'; the first is kept");
                continue;
            }

            var timeout = entry.TimeoutSeconds;
            if (timeout < MinTimeoutSeconds || timeout > MaxTimeoutSeconds)
            {
                problems.Add(
                    $"workspace_checks[{index}] ('{id}') timeout_seconds {timeout} is outside "
                  + $"{MinTimeoutSeconds}–{MaxTimeoutSeconds}; clamped");
                timeout = Math.Clamp(timeout, MinTimeoutSeconds, MaxTimeoutSeconds);
            }

            checks.Add(new CheckDefinition(
                id, command, (entry.Arguments ?? "").Trim(), timeout, entry.Enabled,
                (entry.Description ?? "").Trim() is { Length: > 0 } d ? d : $"operator-declared check '{id}'"));
        }

        return new Result(checks, problems);
    }
}
