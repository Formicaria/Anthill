namespace Anthill.Core.Missions;

/// <summary>
/// WHAT THE OPERATOR ASKED FOR, STRUCTURALLY. v0.3.8.118.
///
/// THE DEFECT THIS EXISTS TO CLOSE. Until now `MissionRequest` carried exactly `{ Goal,
/// IdempotencyKey }`, and a repo-wide search for `requested_roles`, `output_schema` or any
/// equivalent returned nothing in production or test code. So "Anthill ignores the requested role
/// sequence" was never a dispatch bug — there was no input contract to ignore. The only lever an
/// operator had was the natural-language goal string, and the planner was free to read it however
/// it liked.
///
/// Worse, the goal string was ALSO the trigger for spec ingestion: `Planner.IsLongInput` fires at
/// `goal.Length > 6000`, so the more precisely an operator specified roles, ordering and output
/// shape, the more certain it became that the whole request would be chunked into
/// `section_analysis` tasks. Precision was punished. That is the behaviour the live tests found.
///
/// WHAT THIS IS NOT. It is not a second planner and it does not schedule anything. It records what
/// was REQUESTED, in a shape a planner can honour or refuse. The distinction the brief draws is
/// kept here deliberately:
///
///   Label   — what the operator called the step. Free text. Descriptive, never executable.
///   TaskType — a registered type some worker contract declares it supports. Executable.
///   Role     — a registered role id. Not a worker, and not a task type.
///
/// A label is preserved as metadata and RESOLVED to a task type; it is never treated as one. That
/// is the whole reason a mission asking for "deep_competitive_scan" got a researcher doing section
/// analysis instead of an honest refusal: nothing in the runtime distinguished a name someone typed
/// from a name the registry recognises.
///
/// ABSENCE IS THE DEFAULT AND CHANGES NOTHING. <see cref="None"/> means the operator supplied no
/// structured workflow, and every mission that ran before this type existed continues down exactly
/// the path it did. This adds a way to be explicit; it does not make explicitness mandatory.
/// </summary>
public sealed record RequestedWorkflow(
    IReadOnlyList<RequestedTask> Tasks,
    IReadOnlyList<string> RequiredRoles,
    IReadOnlyList<string> OptionalRoles,
    string? OutputSchema,
    string? PermissionMode)
{
    /// <summary>No structured request. The pre-`.118` path, and still the common one.</summary>
    public static readonly RequestedWorkflow None =
        new([], [], [], null, null);

    /// <summary>
    /// True when the operator actually asked for something specific. A workflow with no tasks, no
    /// roles and no schema is indistinguishable from absence and is treated as absence — an empty
    /// object arriving from a client that always sends the field must not switch the runtime into
    /// "honour this exactly" mode and then refuse the mission for requesting nothing.
    /// </summary>
    public bool IsSpecified =>
        Tasks.Count > 0 || RequiredRoles.Count > 0 || !string.IsNullOrWhiteSpace(OutputSchema);

    /// <summary>Every role named anywhere in the request, required or optional or on a task.</summary>
    public IReadOnlyList<string> AllNamedRoles()
    {
        var seen = new List<string>();
        void Add(string? r)
        {
            var v = (r ?? "").Trim();
            if (v.Length == 0) return;
            if (!seen.Contains(v, StringComparer.OrdinalIgnoreCase)) seen.Add(v);
        }
        foreach (var r in RequiredRoles) Add(r);
        foreach (var r in OptionalRoles) Add(r);
        foreach (var t in Tasks) Add(t.Role);
        return seen;
    }
}

/// <summary>
/// One requested step. <paramref name="Label"/> is what the operator called it and is always
/// preserved; <paramref name="TaskType"/> is the registered type they claim it is, and may be null
/// when they left the resolution to the planner.
///
/// `DependsOn` names other steps by LABEL, not by task id — the operator has no task ids at request
/// time, and inventing some for them to reference would be a contract nobody could satisfy.
/// </summary>
public sealed record RequestedTask(
    string Label,
    string? TaskType = null,
    string? Role = null,
    IReadOnlyList<string>? DependsOn = null,
    string? OutputSchema = null)
{
    public IReadOnlyList<string> Dependencies => DependsOn ?? [];
}
