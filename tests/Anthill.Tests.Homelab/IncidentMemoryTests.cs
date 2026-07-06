using Anthill.Core.Common;
using Anthill.Core.Health;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Approvals;
using Anthill.Core.Incidents;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v1.14.0 incident + change memory (NORTH_STAR Phase 10 validation list: incident timeline,
/// similar incident, memory integration, IApprovable design review). Everything is repo-only —
/// incidents track and recommend; nothing remediates.
/// </summary>
public class IncidentMemoryTests : IDisposable
{
    private readonly string _dir;
    private readonly HomelabRepository _repo;
    private readonly IncidentManager _manager;

    public IncidentMemoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_inc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _repo = new HomelabRepository(Path.Combine(_dir, "inc.db"));
        _manager = new IncidentManager(_repo);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- Opening + dedupe + candidate sweep ----------------------------------------------------

    [Fact]
    public void Open_DedupesPerSubject_WhileOpenOrInvestigating()
    {
        var first = _manager.Open("NAS unreachable", "health_check", "nas.lan:445", "warning", "t");
        var second = _manager.Open("NAS still unreachable", "health_check", "nas.lan:445", "warning", "t");
        Assert.Equal(first.Id, second.Id); // same open subject → same incident
        Assert.Single(_repo.ListIncidents());

        _manager.SetStatus(first.Id, "resolved", "cable reseated", "t");
        var third = _manager.Open("NAS unreachable again", "health_check", "nas.lan:445", "warning", "t");
        Assert.NotEqual(first.Id, third.Id); // resolved → a new failure opens a new incident
        Assert.Equal(2, _repo.ListIncidents().Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Sweep_TurnsCandidateEventsIntoIncidents_Idempotently()
    {
        _repo.RecordEvent(new HomelabEvent { EventType = "incident_candidate", SubjectKind = "health_check", SubjectId = "svc-a:80", Severity = "error", Message = "3 consecutive failures" });
        _repo.RecordEvent(new HomelabEvent { EventType = "incident_candidate", SubjectKind = "health_check", SubjectId = "svc-b:443", Severity = "error", Message = "3 consecutive failures" });

        var first = await _manager.SweepAsync(CancellationToken.None);
        Assert.True(first.Ok);
        Assert.Equal(2, first.ItemCount);
        Assert.Equal(2, _repo.ListIncidents().Count);

        var second = await _manager.SweepAsync(CancellationToken.None); // same candidates re-swept
        Assert.Equal(0, second.ItemCount);
        Assert.Equal(2, _repo.ListIncidents().Count);
    }

    [Fact]
    public void RepeatedFailures_FlagPatternAndUpgradeSeverity()
    {
        for (var i = 0; i < 3; i++)
        {
            var inc = _manager.Open($"flap #{i}", "health_check", "flappy:8080", "warning", "t");
            _manager.SetStatus(inc.Id, "resolved", $"restart #{i}", "t");
        }
        var fourth = _manager.Open("flap #3", "health_check", "flappy:8080", "warning", "t");
        Assert.Equal("error", fourth.Severity); // repeat offender opens at error
        Assert.Contains(_repo.RecentEvents(20), e => e.EventType == "incident_pattern");
    }

    // ---- Timeline -----------------------------------------------------------------------------------

    [Fact]
    public void Timeline_FlagsChangesBeforeFailureAsSuspects_AndCorrelatesSubjectActivity()
    {
        // A change lands, then the incident opens, then a failed health result arrives.
        _repo.UpsertService(new ServiceRecord { Id = "svc1", Name = "jellyfin", Owner = "op" }, "deployer");
        var incident = _manager.Open("Jellyfin down", "health_check", "jelly.lan:8096", "warning", "t");
        _repo.SaveHealthResult(new HealthCheckResult { CheckKind = "tcp", Target = "jelly.lan:8096", Status = HealthStatus.Failed, Detail = "refused", CheckedAt = AnthillTime.NowUtc().ToIso() });

        var timeline = _manager.Timeline(incident.Id);
        Assert.NotEmpty(timeline);
        Assert.Contains(timeline, e => e.Kind == "incident" && e.Summary.Contains("Jellyfin down"));
        var change = Assert.Single(timeline, e => e.Kind == "change" && e.Summary.Contains("jellyfin"));
        Assert.True(change.Suspect, "a change in the lookback window before the incident must be marked suspect");
        Assert.Contains(timeline, e => e.Kind == "health" && e.Summary.Contains("refused"));
        // Chronological order.
        var times = timeline.Select(e => e.At).ToList();
        Assert.Equal(times.OrderBy(t => t, StringComparer.Ordinal).ToList(), times);
    }

    [Fact]
    public void Timeline_UnknownIncident_IsEmptyNotThrowing() =>
        Assert.Empty(_manager.Timeline("nope"));

    // ---- Similar incidents + fix memory ---------------------------------------------------------------

    [Fact]
    public void Similar_SurfacesPastFix_SameSubjectScoresHighest()
    {
        var old = _manager.Open("NAS smb share unreachable", "health_check", "nas.lan:445", "warning", "t");
        _manager.SetStatus(old.Id, "resolved", "smbd hung after update — restart smbd and pin samba 4.19", "t");
        var unrelated = _manager.Open("Printer offline", "health_check", "printer.lan:9100", "info", "t");
        _manager.SetStatus(unrelated.Id, "resolved", "paper jam", "t");

        var current = _manager.Open("NAS smb share unreachable again", "health_check", "nas.lan:445", "warning", "t");
        var similar = _manager.Similar(current.Id);

        Assert.NotEmpty(similar);
        Assert.Equal(old.Id, similar[0].Incident.Id); // same subject + overlapping title wins
        Assert.Contains("restart smbd", similar[0].FixedBy); // the fix memory, verbatim
        Assert.DoesNotContain(similar, s => s.Incident.Id == unrelated.Id && s.Score >= similar[0].Score);
    }

    [Fact]
    public void Resolve_RecordsFixMemoryEvent_AndInvalidStatusRejected()
    {
        var incident = _manager.Open("DB corrupt", "manual", "db1", "error", "t");
        Assert.False(_manager.SetStatus(incident.Id, "obliterated", "", "t")); // invalid status
        Assert.True(_manager.SetStatus(incident.Id, "resolved", "restored from backup, enabled WAL", "t"));

        var stored = Assert.Single(_repo.ListIncidents());
        Assert.Equal("resolved", stored.Status);
        Assert.NotEqual("", stored.ResolvedAt);
        Assert.Contains("restored from backup", stored.RootCause);
        Assert.Contains(_repo.RecentEvents(20), e => e.EventType == "incident_fix_recorded");
    }

    // ---- IApprovable design review (Phase 10 validation item) -------------------------------------------

    [Fact]
    public void Approvable_PatchProjection_MapsRowFaithfully()
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = "ap-1", ["action_type"] = "patch_proposal", ["target_id"] = "patch-42",
            ["title"] = "Fix null deref", ["description"] = "Guard the thing", ["status"] = "Pending",
            ["requested_by"] = "coder", ["created_at"] = "2026-07-05T10:00:00Z",
            ["metadata_json"] = """{"risk_level":"HIGH"}""",
        };
        var view = ApprovableProjections.FromPatchApproval(row);
        Assert.Equal("patch:ap-1", view.ApprovableId);
        Assert.Equal("patch", view.Kind);
        Assert.Equal("pending", view.State);
        Assert.Equal("high", view.RiskLevel);
        Assert.Equal("patch_proposal:patch-42", view.DedupeKey);
        Assert.Equal("patch_diff", view.RendererHint);
        Assert.Equal("ap-1", view.SourceId);
    }

    [Fact]
    public void Approvable_OneDedupeRule_NewerPendingSupersedesOlder()
    {
        ApprovableView Make(string id, string createdAt) => new()
        {
            ApprovableId = "patch:" + id, Kind = "patch", State = "pending",
            DedupeKey = "patch_proposal:same-target", CreatedAt = createdAt, SourceId = id,
        };
        var deduped = ApprovableProjections.DedupePending(new[]
        {
            Make("old", "2026-07-01T00:00:00Z"),
            Make("new", "2026-07-05T00:00:00Z"),
        });
        Assert.Equal(2, deduped.Count);
        Assert.Equal("pending", Assert.Single(deduped, v => v.SourceId == "new").State);
        Assert.Equal("superseded", Assert.Single(deduped, v => v.SourceId == "old").State);
    }

    [Fact]
    public void Approvable_ActionProposal_IsInertAndFailsTowardCaution()
    {
        var proposal = new ActionProposal { Title = "Restart jellyfin", ActionType = "restart_service" };
        Assert.Equal("homelab_action", proposal.Kind);
        Assert.Equal("high", proposal.RiskLevel);      // caution by default
        Assert.Equal("pending", proposal.State);        // never born approved
        Assert.Equal("action_proposal", proposal.RendererHint);
        // Design review: the V2.1 blast-radius rubric fields exist and default safe.
        Assert.False(proposal.BackupCovered);
        Assert.False(proposal.DryRunAvailable);
        Assert.Equal("", proposal.RollbackNote);
    }
}
