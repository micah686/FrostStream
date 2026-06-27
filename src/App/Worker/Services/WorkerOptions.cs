namespace Worker.Services;

/// <summary>
/// Configuration for this worker instance's tag-based job routing.
///
/// Workers with an empty <see cref="Tags"/> list and <see cref="AcceptsUntaggedJobs"/> true
/// (the default) behave identically to the pre-tag behaviour: they compete for all
/// download commands on the untagged consumers.
///
/// Workers with tags subscribe to per-tag consumers (e.g.
/// <c>worker-fetch-metadata-nas</c> for tag <c>"nas"</c>), so DataBridge can route
/// storage-affine jobs exclusively to workers that can reach that backend.
/// Setting <see cref="AcceptsUntaggedJobs"/> false on a tagged worker means it will
/// only process jobs explicitly tagged for one of its tags.
/// </summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>Tags this worker instance advertises. Each tag generates a dedicated NATS consumer.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Whether this worker also competes for jobs that have no required worker tag.
    /// Defaults to <see langword="true"/> so untagged deployments are unaffected.
    /// </summary>
    public bool AcceptsUntaggedJobs { get; init; } = true;
}
