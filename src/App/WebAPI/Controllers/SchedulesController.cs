using System.ComponentModel.DataAnnotations;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Quartz;
using Shared.Messaging;

namespace WebAPI.Controllers;

/// <summary>
/// CRUD for scheduled tasks. Round-trips via NATS request/reply to DataBridge's
/// <c>ScheduleCrudConsumerService</c> (which owns the <c>scheduled_tasks</c> table).
/// Mirrors <see cref="OptionPresetsController"/> exactly.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SchedulesController(IMessageBus messageBus, ILogger<SchedulesController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpPost]
    public async Task<ActionResult<ScheduledTaskResponse>> Create(
        [FromBody] ScheduleCreateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            ScheduleSubjects.CreateSchedule,
            new ScheduleCreateRequestMessage
            {
                Key = request.Key,
                TaskType = request.TaskType,
                Cron = request.Cron,
                IntervalSeconds = request.IntervalSeconds,
                Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "UTC" : request.Timezone,
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
            ScheduleSubjects.UpdateSchedule,
            new ScheduleUpdateRequestMessage
            {
                Key = key,
                TaskType = request.TaskType,
                Cron = request.Cron,
                IntervalSeconds = request.IntervalSeconds,
                Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "UTC" : request.Timezone,
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
            ScheduleSubjects.GetSchedule,
            new ScheduleGetRequestMessage { Key = key },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<ScheduledTaskResponse>>> List(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            ScheduleSubjects.ListSchedules,
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
            ScheduleSubjects.DeleteSchedule,
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
                subject, request, RequestTimeout, cancellationToken);
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

    private static ScheduledTaskResponse MapDto(ScheduledTaskDto dto) => new()
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

public abstract class ScheduleRequestBase : IValidatableObject
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public required string TaskType { get; init; }

    public string? Cron { get; init; }

    [Range(1, int.MaxValue)]
    public int? IntervalSeconds { get; init; }

    [StringLength(100)]
    public string? Timezone { get; init; }

    public bool Enabled { get; init; } = true;

    public ScheduleCatchupPolicyDto CatchupPolicy { get; init; } = ScheduleCatchupPolicyDto.Coalesce;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        var hasCron = !string.IsNullOrWhiteSpace(Cron);
        var hasInterval = IntervalSeconds is > 0;
        if (hasCron == hasInterval)
        {
            results.Add(new ValidationResult(
                "Exactly one of cron or intervalSeconds must be set.",
                [nameof(Cron), nameof(IntervalSeconds)]));
        }

        if (hasCron)
        {
            string? cronError = null;
            try
            {
                _ = new CronExpression(Cron!);
            }
            catch (FormatException ex)
            {
                cronError = ex.Message;
            }
            if (cronError is not null)
            {
                results.Add(new ValidationResult(
                    $"Invalid cron expression: {cronError}",
                    [nameof(Cron)]));
            }
        }

        if (!string.IsNullOrWhiteSpace(Timezone))
        {
            var tzError = false;
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(Timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                tzError = true;
            }
            if (tzError)
            {
                results.Add(new ValidationResult(
                    $"Unknown timezone '{Timezone}'.",
                    [nameof(Timezone)]));
            }
        }

        return results;
    }
}

public sealed class ScheduleCreateRequest : ScheduleRequestBase
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }
}

public sealed class ScheduleUpdateRequest : ScheduleRequestBase;

public sealed class ScheduledTaskResponse
{
    public Guid Id { get; init; }
    public required string Key { get; init; }
    public required string TaskType { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public required string Timezone { get; init; }
    public bool Enabled { get; init; }
    public ScheduleCatchupPolicyDto CatchupPolicy { get; init; }
    public Instant? LastAttemptAt { get; init; }
    public Instant? LastSuccessAt { get; init; }
    public Instant? NextDueAt { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}
