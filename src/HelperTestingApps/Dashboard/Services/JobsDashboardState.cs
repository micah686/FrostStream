using Dashboard.Models;

namespace Dashboard.Services;

public sealed class JobsDashboardState(IServiceScopeFactory scopeFactory, ILogger<JobsDashboardState> logger)
{
    private const int MaxJobs = 100;
    private const int MaxActivity = 80;
    private readonly object _gate = new();
    private readonly List<JobSummary> _jobs = [];
    private readonly List<JobActivity> _activity = [];
    private long _sequence;
    private bool _natsConnected;
    private string? _natsStatus = "Starting";
    private DateTimeOffset _lastUpdated = DateTimeOffset.UtcNow;

    public event Func<Task>? Changed;

    public DashboardSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DashboardSnapshot(
                _jobs.ToArray(),
                _activity.ToArray(),
                _lastUpdated,
                _natsConnected,
                _natsStatus);
        }
    }

    public async Task LoadInitialAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<JobQueryService>();
        var jobs = await query.GetRecentJobsAsync(MaxJobs, ct);

        lock (_gate)
        {
            _jobs.Clear();
            _jobs.AddRange(jobs);
            _lastUpdated = DateTimeOffset.UtcNow;
        }

        await NotifyChangedAsync();
    }

    public async Task RecordActivityAsync(JobActivity activity, CancellationToken ct = default)
    {
        var sequenced = activity with { Sequence = Interlocked.Increment(ref _sequence) };
        lock (_gate)
        {
            _activity.Insert(0, sequenced);
            if (_activity.Count > MaxActivity)
            {
                _activity.RemoveRange(MaxActivity, _activity.Count - MaxActivity);
            }
            _lastUpdated = DateTimeOffset.UtcNow;
        }

        if (sequenced.JobId is { } jobId)
        {
            await RefreshJobWithRetryAsync(jobId, ct);
        }

        await NotifyChangedAsync();
    }

    public async Task SetNatsStatusAsync(bool connected, string? status = null)
    {
        lock (_gate)
        {
            _natsConnected = connected;
            _natsStatus = status;
            _lastUpdated = DateTimeOffset.UtcNow;
        }

        await NotifyChangedAsync();
    }

    private async Task RefreshJobWithRetryAsync(Guid jobId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5 && !ct.IsCancellationRequested; attempt++)
        {
            var refreshed = await LoadJobAsync(jobId, ct);
            if (refreshed)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private async Task<bool> LoadJobAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<JobQueryService>();
            var job = await query.GetJobAsync(jobId, ct);
            if (job is null)
            {
                return false;
            }

            lock (_gate)
            {
                var index = _jobs.FindIndex(x => x.JobId == job.JobId);
                if (index >= 0)
                {
                    _jobs[index] = job;
                }
                else
                {
                    _jobs.Insert(0, job);
                }

                _jobs.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                if (_jobs.Count > MaxJobs)
                {
                    _jobs.RemoveRange(MaxJobs, _jobs.Count - MaxJobs);
                }

                _lastUpdated = DateTimeOffset.UtcNow;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed refreshing dashboard job {JobId}", jobId);
            return false;
        }
    }

    private async Task NotifyChangedAsync()
    {
        var changed = Changed;
        if (changed is null)
        {
            return;
        }

        await changed.Invoke();
    }
}

