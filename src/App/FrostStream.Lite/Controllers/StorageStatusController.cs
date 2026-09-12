using Microsoft.AspNetCore.Mvc;
using Shared.Storage;

namespace FrostStream.Lite.Controllers;

[ApiController]
[Route("api/storage")]
public sealed class StorageStatusController(IStorageConfigClient storage) : ControllerBase
{
    [HttpGet("{storageKey}/status")]
    public async Task<IActionResult> GetStatus(string storageKey, CancellationToken cancellationToken)
    {
        var config = await storage.GetStorageConfigAsync(storageKey, cancellationToken);
        return !config.Found
            ? NotFound()
            : Ok(new { config.Key, config.Method, config.Description, config.WorkerTag });
    }
}
