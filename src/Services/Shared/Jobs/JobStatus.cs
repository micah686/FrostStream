using Shared.Entities;

namespace Shared.Jobs;

public enum JobStatus
{
    Unknown = 0,
    Pending = 1,
    Processing = 2,
    UploadedPendingCommit = 3,
    PendingLink = 4,
    Completed = 5,
    Failed = 6,
    NotFound = 7
}

public static class JobStatusCodec
{
    public static JobStatus Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return JobStatus.Unknown;
        }

        return Enum.TryParse<JobStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : JobStatus.Unknown;
    }

    public static string ToStorageValue(this JobStatus status) => status.ToString();
}

public static class JobStateMachine
{
    private static readonly IReadOnlyDictionary<JobStatus, HashSet<JobStatus>> AllowedTransitions =
        new Dictionary<JobStatus, HashSet<JobStatus>>
        {
            [JobStatus.Unknown] =
            [
                JobStatus.Pending,
                JobStatus.Processing,
                JobStatus.UploadedPendingCommit,
                JobStatus.PendingLink,
                JobStatus.Completed,
                JobStatus.Failed
            ],
            [JobStatus.Pending] =
            [
                JobStatus.Processing,
                JobStatus.PendingLink,
                JobStatus.Failed
            ],
            [JobStatus.Processing] =
            [
                JobStatus.UploadedPendingCommit,
                JobStatus.Completed,
                JobStatus.Failed
            ],
            [JobStatus.UploadedPendingCommit] =
            [
                JobStatus.Completed,
                JobStatus.Failed
            ],
            [JobStatus.PendingLink] =
            [
                JobStatus.Completed,
                JobStatus.Failed
            ],
            [JobStatus.Completed] = [JobStatus.Completed],
            [JobStatus.Failed] =
            [
                JobStatus.Processing,
                JobStatus.PendingLink,
                JobStatus.Completed,
                JobStatus.Failed
            ],
            [JobStatus.NotFound] = []
        };

    public static JobStatus Get(Job job) => JobStatusCodec.Parse(job.Status);

    public static bool IsTerminal(JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed;

    public static bool CanTransition(JobStatus from, JobStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return AllowedTransitions.TryGetValue(from, out var allowed)
               && allowed.Contains(to);
    }

    public static void Transition(Job job, JobStatus target)
    {
        var current = Get(job);
        if (!CanTransition(current, target))
        {
            throw new InvalidOperationException(
                $"Invalid job status transition: {current} -> {target} (JobId: {job.JobId})");
        }

        job.Status = target.ToStorageValue();
    }
}
