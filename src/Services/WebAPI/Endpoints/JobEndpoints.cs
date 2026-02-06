using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Messages;

namespace WebAPI;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/jobs - Submit a job for processing
        app.MapPost("/api/jobs", async (
            [FromBody] SubmitJobRequest request,
            IMessageBus messageBus,
            ILogger<Program> logger, // Keeping Logger<Program> for now, or could be Logger<JobEndpoints> if we made it a class
            CancellationToken ct) =>
        {
            var jobId = Guid.NewGuid().ToString("N");

            logger.LogInformation("Submitting job {JobId} for processing", jobId);

            var jobRequest = new ProcessJobRequest
            {
                JobId = jobId,
                SourcePath = request.SourcePath ?? "video.mp4",
                DestinationPath = request.DestinationPath ?? $"output_{jobId}.mp4"
            };

            await messageBus.PublishAsync(Subjects.JobProcess, jobRequest, ct);

            logger.LogInformation("Job {JobId} submitted to workers via NATS", jobId);

            return Results.Accepted($"/api/jobs/{jobId}", new JobSubmittedResponse
            {
                JobId = jobId,
                Status = "Submitted",
                Message = "Job has been submitted to an available worker"
            });
        })
        .WithName("SubmitJob");

        // GET /api/jobs/{jobId} - Get job status (placeholder for future implementation)
        app.MapGet("/api/jobs/{jobId}", (string jobId) =>
        {
            // Future: Implement job status tracking via NATS KV store
            return Results.Ok(new { JobId = jobId, Status = "Unknown", Message = "Job status tracking not yet implemented" });
        })
        .WithName("GetJobStatus");
    }
}

/// <summary>
/// Request body for submitting a new job.
/// </summary>
public record SubmitJobRequest
{
    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }
}

/// <summary>
/// Response when a job is successfully submitted.
/// </summary>
public record JobSubmittedResponse
{
    public required string JobId { get; init; }
    public required string Status { get; init; }
    public required string Message { get; init; }
}
