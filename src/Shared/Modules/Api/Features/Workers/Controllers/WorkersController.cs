using FrostStream.ApplicationContracts;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Workers.Controllers;

[ApiController]
[Route("api/admin/workers")]
public sealed class WorkersController(IMessageBus messageBus) : ControllerBase
{
    [HttpGet]
    [Endpoint(EndpointIds.WorkersList)]
    [EndpointSummary("List registered workers")]
    [EndpointDescription("Returns registered worker instances and their advertised routing tags, capacity, heartbeat timestamps, and current availability for the administrative worker list.")]
    public async Task<ActionResult<WorkerRegistryListResponse>> List([FromQuery] string? tag, CancellationToken cancellationToken)
    {
        var response = await messageBus.RequestAsync<WorkerRegistryListRequest, WorkerRegistryListResponse>(
            WorkerRegistrySubjects.List,
            new WorkerRegistryListRequest { Tag = tag },
            TimeSpan.FromSeconds(5),
            cancellationToken);
        return Ok(response ?? new WorkerRegistryListResponse());
    }

    [HttpPut("{workerId}/capacity")]
    [Endpoint(EndpointIds.WorkersCapacityUpdate)]
    [EndpointSummary("Set worker download capacity")]
    [EndpointDescription("Persists the maximum number of download jobs assigned to a worker. Excess jobs wait without fetching video metadata.")]
    public async Task<ActionResult<WorkerCapacityUpdateResponse>> UpdateCapacity(
        string workerId, [FromBody] WorkerCapacityUpdateRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(workerId, request.WorkerId, StringComparison.Ordinal)
            || request.MaxActiveJobs is < 1 or > 32)
            return BadRequest(new WorkerCapacityUpdateResponse
            {
                Error = "Maximum active jobs must be between 1 and 32 and worker IDs must match."
            });
        var response = await messageBus.RequestAsync<WorkerCapacityUpdateRequest, WorkerCapacityUpdateResponse>(
            WorkerRegistrySubjects.UpdateCapacity, request, TimeSpan.FromSeconds(5), cancellationToken);
        return response is null ? StatusCode(503) : Ok(response);
    }
}
