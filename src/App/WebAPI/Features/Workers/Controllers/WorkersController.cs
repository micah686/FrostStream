using Conduit.NATS;
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
    public async Task<ActionResult<WorkerRegistryListResponse>> List([FromQuery] string? tag, CancellationToken cancellationToken)
    {
        var response = await messageBus.RequestAsync<WorkerRegistryListRequest, WorkerRegistryListResponse>(
            WorkerRegistrySubjects.List,
            new WorkerRegistryListRequest { Tag = tag },
            TimeSpan.FromSeconds(5),
            cancellationToken);
        return Ok(response ?? new WorkerRegistryListResponse());
    }
}
