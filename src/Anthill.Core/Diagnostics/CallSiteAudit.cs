namespace Anthill.Core.Diagnostics;

/// <summary>
/// v3.0.0 baseline lock — "no call site, no feature", enforced.
///
/// The V3 doctrine's third rule states that declarations, schemas, and tests without production
/// wiring are incomplete. V2 proved the rule was needed seven separate times: a recommendation
/// engine with no caller, a skill registry with no table, a handoff gate with zero call sites, a
/// route registration that evaluated a still-running mission and therefore never fired once. Each
/// was caught by a person reading carefully. This turns that reading into a check.
///
/// The audit is a BASELINE, not a bug list. Some declarations legitimately have no in-repo
/// consumer — an HTTP endpoint is consumed by clients, a control-plane role is not dispatched by
/// name. Those live in <see cref="ExpectedOrphans"/> with a stated reason, and the audit fails in
/// BOTH directions: a new orphan is a regression, and an expected orphan that has since acquired a
/// consumer is a stale exemption that must be removed. An allowlist nobody prunes is how a real
/// gap eventually hides inside it.
/// </summary>
public sealed record AuditFinding(string Kind, string Name, string Detail);

public sealed record CallSiteAuditResult(
    IReadOnlyList<AuditFinding> NewOrphans,
    IReadOnlyList<AuditFinding> StaleExemptions)
{
    public bool Clean => NewOrphans.Count == 0 && StaleExemptions.Count == 0;

    public string Explain()
    {
        if (Clean) return "call-site audit clean: every declaration has a production consumer or a current exemption.";
        var lines = new List<string>();
        if (NewOrphans.Count > 0)
        {
            lines.Add($"{NewOrphans.Count} declaration(s) have NO production consumer:");
            lines.AddRange(NewOrphans.Select(o => $"  - [{o.Kind}] {o.Name} — {o.Detail}"));
            lines.Add("  Either wire it to a production call site, or add it to CallSiteAudit.ExpectedOrphans");
            lines.Add("  WITH a written reason. Do not add it silently.");
        }
        if (StaleExemptions.Count > 0)
        {
            lines.Add($"{StaleExemptions.Count} exemption(s) are STALE — these now have consumers and must be");
            lines.Add("removed from ExpectedOrphans so the audit keeps protecting them:");
            lines.AddRange(StaleExemptions.Select(o => $"  - [{o.Kind}] {o.Name} — {o.Detail}"));
        }
        return string.Join("\n", lines);
    }
}

public static class CallSiteAudit
{
    /// <summary>
    /// Declarations that legitimately have no in-repo production consumer, each with the reason it
    /// is exempt. Keyed "kind:name". Adding an entry is a deliberate, reviewable act.
    ///
    /// **Intentionally empty at v3.0.0.** The baseline lock found exactly one orphan in 300
    /// declarations — the dead `cors_enabled` gate — and removed it rather than exempting it. An
    /// empty list is the honest state and the one worth defending: the first entry added here
    /// should have to justify itself against a file that currently says "we needed none".
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ExpectedOrphans =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Run the audit over a built inventory. Endpoints and background loops are skipped: both are
    /// outermost surfaces that are their own call site by construction.
    ///
    /// <paramref name="exemptions"/> defaults to <see cref="ExpectedOrphans"/> and is injectable so
    /// the stale-exemption path can be proven even while the real list is empty — a check that
    /// cannot be shown to fire is not a check.
    /// </summary>
    public static CallSiteAuditResult Run(RuntimeInventoryReport inventory,
        IReadOnlyDictionary<string, string>? exemptions = null)
    {
        var exempt = exemptions ?? ExpectedOrphans;
        var auditable = inventory.Entries
            .Where(e => e.Kind is not (RuntimeInventory.Kinds_Endpoint or RuntimeInventory.Kinds_BackgroundLoop))
            .ToList();

        var newOrphans = auditable
            .Where(e => e.Orphaned && !exempt.ContainsKey(Key(e.Kind, e.Name)))
            .Select(e => new AuditFinding(e.Kind, e.Name, e.Detail))
            .ToList();

        var stale = auditable
            .Where(e => !e.Orphaned && exempt.ContainsKey(Key(e.Kind, e.Name)))
            .Select(e => new AuditFinding(e.Kind, e.Name,
                $"exempt as \"{exempt[Key(e.Kind, e.Name)]}\" but now has {e.CallSites.Count} consumer(s)"))
            .ToList();

        // An exemption naming something the inventory no longer declares is also stale — the
        // declaration was removed and the exemption outlived it.
        var declared = auditable.Select(e => Key(e.Kind, e.Name)).ToHashSet(StringComparer.Ordinal);
        stale.AddRange(exempt
            .Where(kv => !declared.Contains(kv.Key))
            .Select(kv => new AuditFinding(kv.Key.Split(':')[0], kv.Key,
                $"exempt as \"{kv.Value}\" but the runtime no longer declares it")));

        return new CallSiteAuditResult(newOrphans, stale);
    }

    private static string Key(string kind, string name) => $"{kind}:{name}";
}
