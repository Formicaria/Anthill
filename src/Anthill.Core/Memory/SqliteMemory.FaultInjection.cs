using System.Security.Cryptography;
using System.Text;
using Anthill.Core.Common;
using Anthill.Core.Shadow;

namespace Anthill.Core.Memory;

/// <summary>
/// v2.25.0 Phase E closeout — fault-injection runs become a measured series.
///
/// The V3 threshold reads "repeated fault-injection runs stable" — which is only measurable if the
/// runs are REPEATED and RECORDED. Until now `ShadowSimulation.RunAll` executed inside tests and
/// nowhere else: one more well-tested subsystem whose production story was "trust the last CI run".
/// A threshold about run-over-run stability cannot be answered by a harness that keeps no history.
///
/// Stability is defined by fingerprint, not by pass count: two runs that both pass 16/16 but flip
/// WHICH recommendation a scenario produced are not stable — the recommender changed its mind and
/// the count hid it. The fingerprint hashes every scenario's full outcome tuple in catalog order,
/// so any behavioural drift, even pass-preserving drift, breaks the streak.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>Record one full catalog run. Returns the run's behaviour fingerprint.</summary>
    public string SaveFaultInjectionRun(SimulationReport report, DateTime? ranAt = null)
    {
        if (report is null) return "";
        var fingerprint = FaultInjectionFingerprint(report);

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO fault_injection_runs (ran_at, total, passed, all_passed, fingerprint, results_json)
                  VALUES (@at, @total, @passed, @all, @fp, @results)",
                ("@at", (ranAt ?? AnthillTime.NowUtc()).ToIso()),
                ("@total", report.Total), ("@passed", report.Passed),
                ("@all", report.AllPassed ? 1 : 0), ("@fp", fingerprint),
                ("@results", Json.SafeDumps(report.Results)));
        }
        InvalidateCache();
        return fingerprint;
    }

    /// <summary>Most recent runs, newest first.</summary>
    public List<Dictionary<string, object?>> ListFaultInjectionRuns(int limit = 50) =>
        Query(@"SELECT ran_at, total, passed, all_passed, fingerprint FROM fault_injection_runs
                ORDER BY ran_at DESC, id DESC LIMIT @lim",
            ("@lim", Math.Clamp(limit, 1, 500)));

    /// <summary>
    /// The V3-threshold answer. "Stable" requires BOTH: every run in the window passed everything,
    /// AND every run produced byte-identical behaviour (one fingerprint). A window of zero or one
    /// runs is never stable — stability is a property of repetition, and claiming it from a single
    /// sample is exactly the empty-scoreboard-reads-as-passing failure this codebase refuses.
    /// </summary>
    public (int Runs, int StableStreak, bool Stable, string LatestFingerprint) FaultInjectionStability(int window = 10)
    {
        var runs = ListFaultInjectionRuns(Math.Clamp(window, 2, 100));
        if (runs.Count == 0) return (0, 0, false, "");

        var latest = runs[0].GetValueOrDefault("fingerprint")?.ToString() ?? "";
        var streak = 0;
        foreach (var run in runs)   // newest first: streak = consecutive matching runs from the latest
        {
            if ((run.GetValueOrDefault("fingerprint")?.ToString() ?? "") != latest) break;
            if (AsLong(run.GetValueOrDefault("all_passed")) == 0) break;
            streak++;
        }
        return (runs.Count, streak, runs.Count >= 2 && streak == runs.Count, latest);
    }

    /// <summary>
    /// Hash of every scenario's complete outcome tuple, in catalog order. Any change in any
    /// scenario's behaviour — not just its pass flag — produces a different fingerprint.
    /// </summary>
    internal static string FaultInjectionFingerprint(SimulationReport report)
    {
        var sb = new StringBuilder();
        foreach (var r in report.Results)
            sb.Append(r.Name).Append('|').Append(r.Passed).Append('|').Append(r.Safe).Append('|')
              .Append(r.RequiresApproval).Append('|').Append(r.WouldRecommendExecution).Append('|')
              .Append(r.PredictedOutcome).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
