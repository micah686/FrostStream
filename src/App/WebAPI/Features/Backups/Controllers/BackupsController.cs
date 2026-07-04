using Microsoft.AspNetCore.Mvc;
using WebAPI.Auth;
using WebAPI.Features.Backups.Models;

namespace WebAPI.Features.Backups.Controllers;

[ApiController]
[Route("api/admin/backups")]
public sealed class BackupsController(BackupJobService backups) : ControllerBase
{
    [HttpPost]
    [Endpoint(EndpointIds.BackupsCreate)]
    [EndpointSummary("Queue a core-data backup")]
    [EndpointDescription("Starts a background backup job using the configured local backup tool and backup directory. The backup includes FrostStream, Authentik, and OpenFGA PostgreSQL databases plus OpenBao KV secrets, and explicitly excludes media files and rebuildable search or queue state. Mode selects the PostgreSQL strategy: snapshot (default), full, or wal-archive.")]
    public ActionResult<BackupJobResponse> Create([FromBody] CreateBackupRequest? request)
        => Accepted(backups.StartBackup(request?.Name, request?.Mode));

    [HttpGet("jobs")]
    [Endpoint(EndpointIds.BackupsJobsList)]
    [EndpointSummary("List backup jobs")]
    [EndpointDescription("Returns in-memory status for backup jobs started by this WebAPI process, including queued, running, completed, and failed jobs with archive paths or error messages when available.")]
    public ActionResult<IReadOnlyList<BackupJobResponse>> ListJobs()
        => Ok(backups.ListJobs());

    [HttpGet("jobs/{jobId:guid}")]
    [Endpoint(EndpointIds.BackupsJobsGet)]
    [EndpointSummary("Get a backup job")]
    [EndpointDescription("Returns the current status of one backup job started by this WebAPI process, or 404 when the job id is unknown to this running instance.")]
    public ActionResult<BackupJobResponse> GetJob(Guid jobId)
        => backups.GetJob(jobId) is { } job ? Ok(job) : NotFound();

    [HttpGet]
    [Endpoint(EndpointIds.BackupsList)]
    [EndpointSummary("List backup archives")]
    [EndpointDescription("Scans the configured local backup directory for backup manifests and returns archive path, creation time, schema version, and the media exclusion flag for each readable core-data backup.")]
    public ActionResult<IReadOnlyList<BackupSummaryResponse>> ListBackups()
        => Ok(backups.ListBackups());

    [HttpPost("verify")]
    [Endpoint(EndpointIds.BackupsVerify)]
    [EndpointSummary("Verify a backup archive")]
    [EndpointDescription("Runs the configured backup tool's checksum and manifest verification for a local backup archive path and returns whether the backup is readable, compatible, and unmodified.")]
    public async Task<ActionResult<VerifyBackupResponse>> Verify(
        [FromBody] VerifyBackupRequest request)
        => Ok(await backups.VerifyAsync(request.ArchivePath));

    [HttpPost("restore-plan")]
    [Endpoint(EndpointIds.BackupsRestorePlan)]
    [EndpointSummary("Build a cold restore plan")]
    [EndpointDescription("Verifies a local backup archive and returns the exact offline restore command operators should run after stopping FrostStream services. This endpoint does not restore or mutate live state.")]
    public async Task<ActionResult<RestorePlanResponse>> RestorePlan(
        [FromBody] RestorePlanRequest request)
        => Ok(await backups.BuildRestorePlanAsync(request.ArchivePath));
}
