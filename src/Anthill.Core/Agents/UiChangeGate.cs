using System.Text.RegularExpressions;
using Anthill.Core.Domain;
using Anthill.SDK.Artifacts;

namespace Anthill.Core.Agents;

/// <summary>
/// A UI change cannot reach the coder without a valid `ui_map`. v0.3.8.57 — PLAN.md acceptance gate 7.
///
/// WHAT EXISTED, AND WHY IT WAS NOT THE GATE. `Planner.InjectSpecialistRouting` has inserted a
/// cartographer task ahead of the coder since Stage E, and it is useful — but it is not enforcement,
/// for three separate reasons:
///
///   1. It reads the GOAL TEXT only. A goal that says "fix the broken button handler" with a task
///      pointing at `src/Anthill.UI/app.js` matched no keyword and got no map.
///   2. It creates a DEPENDENCY, not a requirement. The coder waited for the cartographer's task to
///      finish — including finishing by failing, or by producing nothing. "Waited for a role" and
///      "has a map" are different claims, and only the second is the one the gate makes.
///   3. It runs at PLANNING time, which is model-influenced and can be bypassed. A structural
///      guarantee cannot live at the point a model gets a say in.
///
/// So this decides at DISPATCH, from the store, and the planner injection stays as the mechanism
/// that makes the map exist. Both call <see cref="LooksLikeUiWork"/>, so they cannot come to disagree
/// about what UI work is — a duplicated keyword list is how a gate ends up guarding a different set
/// than the one the planner routes.
///
/// VALID MEANS VALID. The map must be present, hash-intact, and conform to its schema. An artifact
/// that says `ui_map` and holds a truncated payload is exactly the case a "the artifact exists"
/// check would wave through, and the coder would then plan a change against a map that is not one.
/// </summary>
public static class UiChangeGate
{
    /// <summary>
    /// The goal-text signal. Shared with the planner rather than copied — see the class remarks.
    /// </summary>
    private static readonly string[] GoalWords =
        { "ui", "frontend", "page", "css", "html", "javascript", "dashboard", "canvas" };

    /// <summary>
    /// The PATH signal, which is the half that was missing. Matches paths a task names in its title
    /// or description, so "edit src/Anthill.UI/app.js" is UI work whatever the goal called it.
    ///
    /// Extension-and-directory based rather than a file list: a list would go stale the first time
    /// someone adds a page, and a stale list here means an unmapped UI change that nothing reports.
    /// </summary>
    private static readonly Regex UiPath = new(
        @"[\w./\\-]*(?:\.(?:html?|css|scss|sass|jsx?|tsx?|vue|svelte)\b|[/\\](?:ui|frontend|public|static|assets|components|pages|views|styles)[/\\])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Does this work touch the UI? Answered from the goal AND from the paths the task names.
    ///
    /// Deliberately generous. A false positive costs one read-only mapping task; a false negative is
    /// a UI change proposed blind against a frontend nobody looked at, which is the failure this gate
    /// exists for. Those are not symmetric and the threshold reflects that.
    /// </summary>
    public static bool LooksLikeUiWork(string? goal, string? taskText)
    {
        var lowered = (goal ?? "").ToLowerInvariant();
        if (GoalWords.Any(lowered.Contains)) return true;

        var text = taskText ?? "";
        return UiPath.IsMatch(text);
    }

    /// <summary>A dispatch decision, with the reason an operator needs to act on it.</summary>
    public readonly record struct Decision(bool Allowed, string Reason)
    {
        public static readonly Decision Allow = new(true, "");
    }

    /// <summary>
    /// May this task be dispatched?
    ///
    /// Only ever refuses a CODER, and only for UI work. Every other role and every non-UI change is
    /// allowed through untouched — a gate that widened past its subject would start blocking work it
    /// was never reasoning about.
    /// </summary>
    public static Decision Check(Task task, Mission mission, IArtifactStore? artifacts,
        bool cartographerAvailable)
    {
        if (task is null || !string.Equals(task.AssignedAnt, "coder", StringComparison.OrdinalIgnoreCase))
            return Decision.Allow;

        if (!LooksLikeUiWork(mission?.Goal, $"{task.Title} {task.Description}"))
            return Decision.Allow;

        // No store means no way to check, and refusing on that basis would block every caller that
        // constructs a coder without one — dozens of tests and the CLI. The gate reports what it can
        // verify; it does not fail closed on its own absence, because that is not evidence about the
        // mission, it is evidence about the wiring.
        if (artifacts is null) return Decision.Allow;

        if (!cartographerAvailable)
            return new Decision(false,
                "this task changes the UI and the ui_cartographer is not available, so no frontend map "
              + "can be produced. Enable the ui_cartographer role, or narrow the task to non-UI work — "
              + "a UI change proposed without a map is the failure PLAN.md gate 7 names.");

        List<Artifact> maps;
        try { maps = artifacts.ForMission(mission!.Id, ArtifactSchemas.UiMap).ToList(); }
        catch (Exception error)
        {
            // v0.3.8.64 (PLAN.md §1b S6): a store that THROWS is not a store that is absent — the
            // same distinction the verifier learned in S3. Absent is the CLI and the tests, and
            // that arm (above) stays permissive because it is evidence about the wiring. But
            // production dispatch always has a store, so a store that exists and cannot answer is
            // an incident, and letting a UI change through BECAUSE the check machinery is down is
            // the gate failing open at the exact moment it is needed most.
            Console.Error.WriteLine($"[ui-gate] could not read ui_map artifacts for {mission?.Id}: {error.Message}");
            return new Decision(false,
                $"this task changes the UI and the artifact store could not be read ({error.Message}). "
              + "The gate cannot verify a map exists, and unverifiable is not allowed — retry after "
              + "the store recovers.");
        }

        if (maps.Count == 0)
            return new Decision(false,
                "this task changes the UI and the mission has no ui_map. The cartographer must map the "
              + "frontend before a change is proposed against it.");

        // PRESENT is not VALID. A truncated or mistyped payload under a `ui_map` label is precisely
        // what an existence check waves through, and the coder would then plan against a map that
        // is not one.
        var usable = maps.FirstOrDefault(m => m.IsIntact()
            && ArtifactSchemaCheck.Validate(m.Schema, m.Payload).Conforms);

        return usable is not null
            ? Decision.Allow
            : new Decision(false,
                $"this task changes the UI and the mission's {maps.Count} ui_map artifact(s) are not "
              + "usable: each is either mutated relative to its recorded hash or does not conform to "
              + "the ui_map schema. A map that cannot be trusted is worse than none, because the coder "
              + "would plan against it anyway.");
    }
}
