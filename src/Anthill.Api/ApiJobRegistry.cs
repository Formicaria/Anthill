using System.Collections.Concurrent;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Orchestration;

namespace Anthill.Api;

public sealed class ApiMissionJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Goal { get; init; } = "";
    public string Status { get; set; } = "queued"; // queued | running | complete | failed
    public string? MissionId { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; init; } = AnthillTime.NowUtc();
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public Dictionary<string, object?> ToDict() => new()
    {
        ["id"] = Id, ["goal"] = Goal, ["status"] = Status, ["mission_id"] = MissionId,
        ["result"] = Result, ["error"] = Error, ["created_at"] = CreatedAt.ToIso(),
        ["started_at"] = StartedAt.ToIsoOrNull(), ["finished_at"] = FinishedAt.ToIsoOrNull(),
    };
}

/// <summary>
/// Bounded, in-process mission job runner. Missions submitted through the API are queued
/// and executed by a small worker pool (one worker by default, matching the Python build's
/// single-writer posture) so the HTTP request returns immediately while the colony works.
/// History is trimmed to a fixed cap to keep memory bounded.
/// </summary>
public sealed class ApiJobRegistry : IDisposable
{
    private readonly Queen _queen;
    private readonly BlockingCollection<ApiMissionJob> _queue = new();
    private readonly ConcurrentDictionary<string, ApiMissionJob> _jobs = new();
    private readonly ConcurrentQueue<string> _order = new();
    private readonly List<Thread> _workers = new();
    private readonly object _trimLock = new();

    public ApiJobRegistry(Queen queen, int workers)
    {
        _queen = queen;
        for (var i = 0; i < Math.Max(1, workers); i++)
        {
            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = $"anthill-job-worker-{i}" };
            worker.Start();
            _workers.Add(worker);
        }
    }

    public ApiMissionJob Submit(string goal)
    {
        var job = new ApiMissionJob { Goal = goal };
        _jobs[job.Id] = job;
        _order.Enqueue(job.Id);
        TrimLocked();
        _queue.Add(job);
        return job;
    }

    private void WorkerLoop()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            job.Status = "running";
            job.StartedAt = AnthillTime.NowUtc();
            try
            {
                job.Result = _queen.RunMission(job.Goal);
                job.MissionId = _queen.LastMissionId;
                job.Status = "complete";
            }
            catch (Exception error)
            {
                job.Error = error.Message;
                job.Status = "failed";
            }
            finally
            {
                job.FinishedAt = AnthillTime.NowUtc();
            }
        }
    }

    public List<Dictionary<string, object?>> ListJobs(int limit = 50) =>
        _jobs.Values.OrderByDescending(j => j.CreatedAt).Take(limit).Select(j => j.ToDict()).ToList();

    public ApiMissionJob? GetJob(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    private void TrimLocked()
    {
        lock (_trimLock)
        {
            while (_jobs.Count > AnthillRuntime.ApiJobMaxHistory && _order.TryDequeue(out var oldest))
                _jobs.TryRemove(oldest, out _);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _queue.Dispose();
    }
}
