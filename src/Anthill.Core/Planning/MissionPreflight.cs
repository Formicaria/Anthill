using Anthill.Core.Agents;
using Anthill.Core.Missions;

namespace Anthill.Core.Planning;

/// <summary>
/// IS THIS PLAN CAPABLE OF PRODUCING WHAT WAS ASKED FOR? v0.3.8.104.
///
/// WHAT IT IS FOR. Every gate this program has built so far runs AFTER the work: the assessment
/// objective, citation integrity, operation integrity, the send gate. They answer "did the mission
/// deliver", which is the right question and the expensive place to ask it — a mission whose plan
/// could never have delivered still burns its model calls, its tool dispatches and the operator's
/// wait before anything says so. Preflight asks the answerable half BEFORE execution: not whether
/// the answer will be good, but whether anything in this plan is even positioned to produce it.
///
/// IT RUNS AFTER `EnsureClassCoverage`, and that ordering is the whole reason it is safe. Coverage
/// SUPPLIES what a class requires and the planner omitted — `.98` recorded this deliberately, when
/// the exit line said "a missing builder fails preflight" and the implementation supplied the
/// builder instead, because refusing a plan for lacking a step the runtime knows how to add
/// punishes the operator for the planner's omission. Preflight therefore refuses only what the
/// runtime CANNOT repair. Anything it rejects that coverage could have added is a bug in coverage,
/// not a plan the operator should have to fix.
///
/// EVERY REFUSAL NAMES THE THING. "Preflight failed" is a message an operator cannot act on; "the
/// audit requires inspect_runtime_state and no worker in the researcher role declares it" is one
/// they can. That is the standing rule this repository keeps paying to relearn — a failure message
/// must name the layer that said no, and `.99` found the explanation had never even been persisted.
///
/// WHAT IT DOES NOT DO. It does not judge quality, ordering, or whether the plan is a GOOD way to
/// answer the request. Those are model judgments and stay outside, the standing line. It asks only
/// questions a record can answer: does a producer exist, does a verifier exist, can this worker be
/// resolved, does this dependency point at a real task, is anything unreachable.
/// </summary>
public static class MissionPreflight
{
    /// <summary>One reason a plan cannot proceed, with the id it is about.</summary>
    /// <param name="Code">Stable, for consumers that branch. Never prose.</param>
    /// <param name="Subject">The deliverable id, task id or capability the refusal is about.</param>
    /// <param name="Detail">What an operator would need to read to act on it.</param>
    public sealed record Blocker(string Code, string Subject, string Detail)
    {
        public override string ToString() => $"{Code} [{Subject}]: {Detail}";
    }

    /// <summary>The codes, spelled once — consumers branch on these rather than on message text.</summary>
    public static class Codes
    {
        public const string UnproducedDeliverable = "unproduced_deliverable";
        public const string UnverifiedCriterion = "unverified_criterion";
        public const string MissingCapability = "missing_capability";
        public const string InvalidDependency = "invalid_dependency";
        public const string OrphanedTask = "orphaned_task";
    }

    public sealed record Result(IReadOnlyList<Blocker> Blockers)
    {
        public bool Ok => Blockers.Count == 0;

        /// <summary>True when the plan cannot run because the COLONY lacks something, as opposed to
        /// because the plan is malformed. The two want different operator responses, and the
        /// outcome vocabulary distinguishes them: one is `blocked_missing_capability`, the other is
        /// a plan that should be recompiled.</summary>
        public bool IsCapabilityBlocked =>
            Blockers.Any(b => b.Code == Codes.MissingCapability);

        public string Explanation => Ok
            ? "preflight: every deliverable has a producer, every criterion a verifier, every task a worker"
            : "preflight refused this plan — " + string.Join("; ", Blockers.Select(b => b.ToString()));
    }

    private static readonly Result Passed = new(Array.Empty<Blocker>());

    /// <summary>
    /// Check a compiled plan against the mission's contract.
    /// </summary>
    /// <param name="tasks">The plan AFTER class coverage has supplied what it supplies.</param>
    /// <param name="specification">What the operator asked for. A permissive specification checks
    /// only the structural half — an unclassified mission declares no deliverables to produce and
    /// no capabilities to serve, so there is nothing for the first two checks to be about, and
    /// inventing requirements for it would refuse missions that have always run.</param>
    public static Result Check(IReadOnlyList<Domain.Task> tasks, MissionSpecification? specification)
    {
        if (tasks is null || tasks.Count == 0)
            return new Result(new[]
            {
                new Blocker(Codes.OrphanedTask, "plan",
                    "the compiled plan has no tasks, so nothing can produce anything"),
            });

        var blockers = new List<Blocker>();
        var ids = tasks.Select(t => t.Id).Where(i => !string.IsNullOrWhiteSpace(i))
            .ToHashSet(StringComparer.Ordinal);

        // ---- structural: dependencies point at real tasks, nothing is unreachable ---------------
        foreach (var task in tasks)
        {
            foreach (var dep in task.DependsOn)
            {
                if (string.IsNullOrWhiteSpace(dep)) continue;
                if (!ids.Contains(dep))
                    blockers.Add(new Blocker(Codes.InvalidDependency, Short(task.Id),
                        $"'{task.Title}' depends on task '{dep}', which is not in this plan. A "
                      + "dependency on a task that does not exist can never be satisfied, so the "
                      + "task can never run."));
                else if (string.Equals(dep, task.Id, StringComparison.Ordinal))
                    blockers.Add(new Blocker(Codes.InvalidDependency, Short(task.Id),
                        $"'{task.Title}' depends on itself."));
            }

            // A task with no role cannot be dispatched to anything.
            if (string.IsNullOrWhiteSpace(task.AssignedAnt))
                blockers.Add(new Blocker(Codes.OrphanedTask, Short(task.Id),
                    $"'{task.Title}' names no role, so no ant can be asked to run it."));
        }

        // ---- capability: a task the runtime created FOR a capability must have a server ---------
        //
        // Only tasks carrying their own RequiredCapability, for the reason WorkerResolution gives:
        // a task measured against the mission-wide list is advisory, and a role outside the class's
        // list is legitimate rather than a blocker.
        foreach (var task in tasks.Where(t => !string.IsNullOrWhiteSpace(t.RequiredCapability)))
        {
            var (worker, _) = AntRegistry.ResolveByCapability(
                task.AssignedAnt ?? "", new[] { task.RequiredCapability! });

            if (worker is null)
                blockers.Add(new Blocker(Codes.MissingCapability, task.RequiredCapability!,
                    $"'{task.Title}' requires the capability '{task.RequiredCapability}' and no "
                  + $"worker in the '{task.AssignedAnt}' role declares it. The colony cannot do "
                  + "this, and running the mission would produce a confident answer from a worker "
                  + "that was never able to serve the request."));
        }

        // NO "MUST HAVE A BUILDER" CHECK, and its removal is the sharpest thing this class
        // learned. The first draft refused any plan without a builder task — and refused the
        // ENTIRE CODING LANE with it: a patch mission plans coder, tester and soldier, and its
        // answer is composed by `ResultAssembler` from the best completed task, which has never
        // needed a builder step to exist. Ten composed lifecycle tests failed on
        // `no_assembly_stage` for missions that have worked since the beginning.
        //
        // That is precisely the error `.98` recorded and this class's own header promises not to
        // repeat: refusing a plan for lacking a step the runtime does not actually require. A
        // recognized class gets its builder from `EnsureClassCoverage` before preflight ever runs,
        // so the check was vacuous where it was safe and destructive everywhere else.

        if (specification is null || !specification.IsActionable) return Finish(blockers);

        // ---- every requested deliverable has something that could produce it --------------------
        //
        // STRUCTURAL, exactly as `.98` established when it refused to grade coverage by vocabulary:
        // a deliverable is served when a task CLAIMS it, or — when the plan claimed nothing — by
        // the assembling task, which is the honest weaker reading the ledger already records as
        // `inferred`. What this refuses is the case no reading rescues: a plan that claims
        // deliverables and leaves one claimed by nothing.
        var claimed = tasks.SelectMany(t => t.DeliverableIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (claimed.Count > 0)
            foreach (var deliverable in specification.Deliverables)
                if (!claimed.Contains(deliverable.Id))
                    blockers.Add(new Blocker(Codes.UnproducedDeliverable, deliverable.Id,
                        $"the plan attributes deliverables to tasks but nothing claims "
                      + $"'{deliverable.Id}' — the operator asked \"{Trim(deliverable.Request)}\" "
                      + "and no step is answerable for it."));

        // ---- and a class that must be verified has a verifier ------------------------------------
        //
        // The success criterion for every recognized class is its integrity gate, and every one of
        // those gates reads what a VERIFIER consumed. A recognized mission planned without one
        // cannot satisfy its own class, and finding that out after execution wastes the run.
        if (MissionContracts.RecognizedClasses.Contains(specification.MissionClass)
            && !tasks.Any(t => string.Equals(t.AssignedAnt, "verifier", StringComparison.OrdinalIgnoreCase)))
            blockers.Add(new Blocker(Codes.UnverifiedCriterion, specification.MissionClass,
                $"'{specification.MissionClass}' is objectively verified and this plan has no "
              + "verifier, so its integrity gate could never be satisfied however well the work "
              + "went."));

        return Finish(blockers);
    }

    private static Result Finish(List<Blocker> blockers) =>
        blockers.Count == 0 ? Passed : new Result(blockers);

    private static string Short(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "unnamed" : id!.Length <= 8 ? id! : id![..8];

    private static string Trim(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" :
        text!.Length <= 80 ? text.Trim() : text[..80].Trim() + "…";
}
