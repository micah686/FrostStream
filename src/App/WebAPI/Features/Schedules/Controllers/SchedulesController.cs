using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Shared.Database;
using Shared.Messaging;
using WebAPI.Features.Schedules.Models;

namespace WebAPI.Features.Schedules.Controllers;

[ApiController]
[Route("api/schedules")]
public sealed class SchedulesController(IMessageBus messageBus, ILogger<SchedulesController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpPost]
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
    public async Task<ActionResult<ScheduledTaskResponse>> Get(string key, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            ScheduleSubjects.Get,
            new ScheduleGetRequestMessage { Key = key },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet]
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
