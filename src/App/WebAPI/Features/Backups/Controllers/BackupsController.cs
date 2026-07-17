using Microsoft.AspNetCore.Mvc;
using WebAPI.Auth;
using WebAPI.Features.Backups.Models;

namespace WebAPI.Features.Backups.Controllers;

[ApiController]
[Route("api/global/backups")]
public sealed class BackupsController(BackupJobService backups) : ControllerBase
{
    [HttpPost]
    [Endpoint(EndpointIds.BackupsCreate)]
    [EndpointSummary("Queue a core-data backup")]
    [EndpointDescription("Queues a job in the dedicated backup service. The backup includes FrostStream, Authentik, and OpenFGA PostgreSQL databases plus OpenBao KV secrets, and explicitly excludes media files and rebuildable search or queue state. Mode selects the PostgreSQL strategy: snapshot (default), full, or wal-archive.")]
    public async Task<ActionResult<BackupJobResponse>> Create(
        [FromBody] CreateBackupRequest? request,
        CancellationToken cancellationToken)
        => Accepted(await backups.StartBackupAsync(request?.Name, request?.Mode, cancellationToken));

    [HttpGet("jobs")]
    [Endpoint(EndpointIds.BackupsJobsList)]
    [EndpointSummary("List backup jobs")]
    [EndpointDescription("Returns durable status from the backup service, including queued, running, completed, failed, and interrupted jobs with archive paths or errors when available.")]
    public async Task<ActionResult<IReadOnlyList<BackupJobResponse>>> ListJobs(CancellationToken cancellationToken)
        => Ok(await backups.ListJobsAsync(cancellationToken));

    [HttpGet("jobs/{jobId:guid}")]
    [Endpoint(EndpointIds.BackupsJobsGet)]
    [EndpointSummary("Get a backup job")]
    [EndpointDescription("Returns the current status of one backup job started by this WebAPI process, or 404 when the job id is unknown to this running instance.")]
    public async Task<ActionResult<BackupJobResponse>> GetJob(Guid jobId, CancellationToken cancellationToken)
        => await backups.GetJobAsync(jobId, cancellationToken) is { } job ? Ok(job) : NotFound();

    [HttpGet]
    [Endpoint(EndpointIds.BackupsList)]
    [EndpointSummary("List backup archives")]
    [EndpointDescription("Scans the configured local backup directory for backup manifests and returns archive path, creation time, schema version, and the media exclusion flag for each readable core-data backup.")]
    public async Task<ActionResult<IReadOnlyList<BackupSummaryResponse>>> ListBackups(CancellationToken cancellationToken)
        => Ok(await backups.ListBackupsAsync(cancellationToken));

    [HttpPost("verify")]
    [Endpoint(EndpointIds.BackupsVerify)]
    [EndpointSummary("Verify a backup archive")]
    [EndpointDescription("Runs the configured backup tool's checksum and manifest verification for a local backup archive path and returns whether the backup is readable, compatible, and unmodified.")]
    public async Task<ActionResult<VerifyBackupResponse>> Verify(
        [FromBody] VerifyBackupRequest request,
        CancellationToken cancellationToken)
        => Ok(await backups.VerifyAsync(request.ArchivePath, cancellationToken));

    [HttpPost("restore-plan")]
    [Endpoint(EndpointIds.BackupsRestorePlan)]
    [EndpointSummary("Build a cold restore plan")]
    [EndpointDescription("Verifies a local backup archive and returns the exact offline restore command operators should run after stopping FrostStream services. This endpoint does not restore or mutate live state.")]
    public async Task<ActionResult<RestorePlanResponse>> RestorePlan(
        [FromBody] RestorePlanRequest request,
        CancellationToken cancellationToken)
        => Ok(await backups.BuildRestorePlanAsync(request.ArchivePath, cancellationToken));
}
