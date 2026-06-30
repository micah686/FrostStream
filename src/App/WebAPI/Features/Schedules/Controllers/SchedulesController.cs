using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Database;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Schedules.Models;

namespace WebAPI.Features.Schedules.Controllers;

[ApiController]
[Route("api/schedules")]
public sealed class SchedulesController(IMessageBus messageBus, ILogger<SchedulesController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpPost]
    [Endpoint(EndpointIds.SchedulesCreate)]
    [EndpointSummary("Create a scheduled background task")]
    [EndpointDescription("Creates a scheduler definition for the requested task type using either cron or interval timing, timezone, enabled state, and catch-up policy. DataBridge validates the schedule and persists it; duplicate keys return 409 and invalid timing combinations return 400.")]
    public async Task<ActionResult<ScheduledTaskResponse>> Create(
        [FromBody] ScheduleCreateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            ScheduleSubjects.Create,
            new ScheduleCreateRequestMessage
            {
                Key = request.Key,
                TaskType = request.TaskType,
                Cron = request.Cron,
                IntervalSeconds = request.IntervalSeconds,
                Timezone = request.Timezone,
                Enabled = request.Enabled,
                CatchupPolicy = request.CatchupPolicy
            },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpPut("{key}")]
    [Endpoint(EndpointIds.SchedulesUpdate)]
    [EndpointSummary("Update a scheduled background task")]
    [EndpointDescription("Replaces the configuration of an existing scheduled task identified by key. The task type, timing, timezone, enabled state, and catch-up policy are revalidated and persisted while execution timestamps are returned from the stored schedule.")]
    public async Task<ActionResult<ScheduledTaskResponse>> Update(
        string key,
        [FromBody] ScheduleUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            ScheduleSubjects.Update,
            new ScheduleUpdateRequestMessage
            {
                Key = key,
                TaskType = request.TaskType,
                Cron = request.Cron,
                IntervalSeconds = request.IntervalSeconds,
                Timezone = request.Timezone,
                Enabled = request.Enabled,
                CatchupPolicy = request.CatchupPolicy
            },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet("{key}")]
    [Endpoint(EndpointIds.SchedulesGet)]
    [EndpointSummary("Get a scheduled background task")]
    [EndpointDescription("Retrieves a single scheduler definition by key, including its timing configuration, enabled state, last attempt and success timestamps, and next due time. Returns 404 for an unknown key and 503 when DataBridge is unavailable.")]
    public async Task<ActionResult<ScheduledTaskResponse>> Get(string key, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            ScheduleSubjects.Get,
            new ScheduleGetRequestMessage { Key = key },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet]
    [Endpoint(EndpointIds.SchedulesList)]
    [EndpointSummary("List scheduled background tasks")]
    [EndpointDescription("Returns every persisted scheduler definition with its configuration and runtime status timestamps. The request is served through DataBridge request/reply and returns 503 if the schedule service does not respond within the configured timeout.")]
    public async Task<ActionResult<IReadOnlyCollection<ScheduledTaskResponse>>> List(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            ScheduleSubjects.List,
            new ScheduleListRequestMessage(),
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process schedule list request.");
        if (!response.Success)
            return MapErrorResponse(response);

        return Ok((response.Items ?? Array.Empty<ScheduledTaskDto>()).Select(MapDto).ToArray());
    }

    [HttpDelete("{key}")]
    [Endpoint(EndpointIds.SchedulesDelete)]
    [EndpointSummary("Delete a scheduled background task")]
    [EndpointDescription("Deletes the scheduler definition identified by its key so it is no longer eligible for future execution. Successful deletion returns 204; missing schedules return 404 and conflicting deletions return 409.")]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            ScheduleSubjects.Delete,
            new ScheduleDeleteRequestMessage { Key = key },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process schedule delete request.");
        if (!response.Success)
            return MapErrorResponse(response);

        return NoContent();
    }

    private async Task<ScheduleOperationResponseMessage?> SendAsync<TRequest>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, ScheduleOperationResponseMessage>(
                subject,
                request,
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing schedule request on subject '{Subject}'", subject);
            return null;
        }
    }

    private ActionResult<ScheduledTaskResponse> MapEntityResponse(ScheduleOperationResponseMessage? response)
    {
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process schedule request.");
        if (!response.Success)
            return MapErrorResponse(response);
        if (response.Entity is null)
            return StatusCode(StatusCodes.Status502BadGateway, "Schedule service returned an invalid response.");

        return Ok(MapDto(response.Entity));
    }

    private ActionResult MapErrorResponse(ScheduleOperationResponseMessage response)
        => response.ErrorCode switch
        {
            "not_found" => NotFound(response.ErrorMessage),
            "conflict" => Conflict(response.ErrorMessage),
            "validation" => BadRequest(response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Schedule request failed.")
        };

    private static ScheduledTaskResponse MapDto(ScheduledTaskDto dto)
        => new()
        {
            Id = dto.Id,
            Key = dto.Key,
            TaskType = dto.TaskType,
            Cron = dto.Cron,
            IntervalSeconds = dto.IntervalSeconds,
            Timezone = dto.Timezone,
            Enabled = dto.Enabled,
            CatchupPolicy = dto.CatchupPolicy,
            LastAttemptAt = dto.LastAttemptAt,
            LastSuccessAt = dto.LastSuccessAt,
            NextDueAt = dto.NextDueAt,
            CreatedAt = dto.CreatedAt,
            LastUpdated = dto.LastUpdated
        };
}
