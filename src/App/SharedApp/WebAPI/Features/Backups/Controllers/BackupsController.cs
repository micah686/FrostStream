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
    [EndpointDescription("Creates a backup using the deployment adapter. Full supports PostgreSQL full/differential backups with paired OpenBao exports. Lite supports verified full SQLite snapshots of application, workflow, queue, staged-object, and encrypted-secret data. Unsupported operations return 400; media files are managed separately.")]
    public async Task<ActionResult<BackupJobResponse>> Create(
        [FromBody] CreateBackupRequest? request,
        CancellationToken cancellationToken)
        => Accepted(await backups.StartBackupAsync(request?.Name, request?.Type, cancellationToken));

    [HttpGet("jobs")]
    [Endpoint(EndpointIds.BackupsJobsList)]
    [EndpointSummary("List backup jobs")]
    [EndpointDescription("Returns status from the selected backup adapter for its supported jobs, including queued, running, completed, and failed states with the produced backup label or error when available.")]
    public async Task<ActionResult<IReadOnlyList<BackupJobResponse>>> ListJobs(CancellationToken cancellationToken)
        => Ok(await backups.ListJobsAsync(cancellationToken));

    [HttpGet("jobs/{jobId:guid}")]
    [Endpoint(EndpointIds.BackupsJobsGet)]
    [EndpointSummary("Get a backup job")]
    [EndpointDescription("Returns the current status of one backup-service job, including its live output tail, or 404 when the job id is unknown.")]
    public async Task<ActionResult<BackupJobResponse>> GetJob(Guid jobId, CancellationToken cancellationToken)
        => await backups.GetJobAsync(jobId, cancellationToken) is { } job ? Ok(job) : NotFound();

    [HttpGet]
    [Endpoint(EndpointIds.BackupsList)]
    [EndpointSummary("List backups in the repository")]
    [EndpointDescription("Returns backups, sizes, timestamps, and repository status from the deployment adapter. Lite also returns its resolved database and backup paths. WAL, OpenBao pairing, and point-in-time recovery metadata apply to Full.")]
    public async Task<ActionResult<BackupRepositoryResponse>> ListBackups(CancellationToken cancellationToken)
        => Ok(await backups.ListBackupsAsync(cancellationToken));

    [HttpPost("verify")]
    [Endpoint(EndpointIds.BackupsVerify)]
    [EndpointSummary("Verify backups")]
    [EndpointDescription("Returns a verification job for polling. Full supports repository checksum checks and deep test-restoration. Lite checks SQLite integrity for the selected snapshot or the latest when no label is supplied. Unsupported deep verification returns 400.")]
    public async Task<ActionResult<BackupJobResponse>> Verify(
        [FromBody] VerifyBackupRequest? request,
        CancellationToken cancellationToken)
        => Accepted(await backups.VerifyAsync(request?.Label, request?.Deep ?? false, cancellationToken));
}
