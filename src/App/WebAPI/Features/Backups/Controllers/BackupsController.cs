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
    [EndpointDescription("Queues a pgBackRest backup in the dedicated backup service. The repository covers the FrostStream, Authentik, and OpenFGA PostgreSQL databases plus a paired OpenBao KV secrets export; media files and rebuildable search or queue state are excluded. Type selects the backup kind: full or diff (differential against the last full).")]
    public async Task<ActionResult<BackupJobResponse>> Create(
        [FromBody] CreateBackupRequest? request,
        CancellationToken cancellationToken)
        => Accepted(await backups.StartBackupAsync(request?.Name, request?.Type, cancellationToken));

    [HttpGet("jobs")]
    [Endpoint(EndpointIds.BackupsJobsList)]
    [EndpointSummary("List backup jobs")]
    [EndpointDescription("Returns durable status from the backup service for backup, verify, and restore jobs, including queued, running, completed, and failed states with the produced backup label or error when available.")]
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
    [EndpointDescription("Returns the pgBackRest repository contents: each backup's label, type, user-supplied name, timestamps, sizes, WAL range, and whether its paired OpenBao export is present, plus the repository health and the point-in-time recovery window.")]
    public async Task<ActionResult<BackupRepositoryResponse>> ListBackups(CancellationToken cancellationToken)
        => Ok(await backups.ListBackupsAsync(cancellationToken));

    [HttpPost("verify")]
    [Endpoint(EndpointIds.BackupsVerify)]
    [EndpointSummary("Verify backups")]
    [EndpointDescription("Queues a verification job and returns it for polling. Quick verify checks every backup and WAL checksum in the repository. Deep verify additionally test-restores one backup (the given label, or the latest) into a scratch area, starts a throwaway PostgreSQL, and confirms the expected databases and tables are present.")]
    public async Task<ActionResult<BackupJobResponse>> Verify(
        [FromBody] VerifyBackupRequest? request,
        CancellationToken cancellationToken)
        => Accepted(await backups.VerifyAsync(request?.Label, request?.Deep ?? false, cancellationToken));
}
