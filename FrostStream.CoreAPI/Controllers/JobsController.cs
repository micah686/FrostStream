using FrostStream.CoreAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace FrostStream.CoreAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly JobQueueService _queue;

    public JobsController(JobQueueService queue)
    {
        _queue = queue;
    }

    [HttpPost]
    public IActionResult CreateJob([FromBody] string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return BadRequest("Payload must not be empty");

        var jobId = _queue.EnqueueJob(payload);
        return Ok(new { JobId = jobId, Status = "Queued" });
    }
}
