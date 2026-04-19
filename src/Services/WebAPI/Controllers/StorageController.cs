using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared;
using Shared.Messaging;

namespace WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StorageController : ControllerBase
{
    
    private readonly IMessageBus _messageBus;
    private readonly ILogger<StorageController> _logger;

    public StorageController(IMessageBus messageBus, ILogger<StorageController> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    // static StorageController()
    // {
    //     StorageItems.TryAdd(1, new StorageConfigEntity
    //     {
    //         Id = 1,
    //         Key = "default",
    //         Method = StorageMethod.PosixLocal,
    //         Parameters = "{\"path\":\"./data/\"}",
    //         Description = "Fallback/Default Local Storage",
    //         CreatedAt = new Instant()
    //     });
    // }

    [HttpPost("create")]
    public async Task<ActionResult<StorageConfigEntity>> CreateStorage([FromBody] CreateStorageRequest request, CancellationToken cancellationToken)
    {

        var entity = new StorageConfigEntity
        {
            Key = request.Key,
            Method = request.Method,
            Parameters = request.Parameters,
            Description = request.Description,
            CreatedAt = new Instant(),
            UpdatedAt = null
        };

        // if (StorageItems.Values.Any(x => x.Key.Equals(entity.Key, StringComparison.OrdinalIgnoreCase)))
        // {
        //     return Conflict($"Storage key '{entity.Key}' already exists.");
        // }
        

        try
        {
            await _messageBus.PublishAsync(
                StorageSubjects.CreateStorage,
                new CreateStorageMessage
                {
                    Key = entity.Key,
                    Method = entity.Method,
                    Parameters = entity.Parameters,
                    Description = entity.Description,
                    RequestedAtUtc = new Instant()
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            
            _logger.LogError(ex, "Failed publishing create-storage message for key '{StorageKey}'", entity.Key);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to publish storage create message.");
        }
        
        return CreatedAtAction(nameof(GetStorage), entity);
    }

    [HttpPut("update/{id:int}")]
    public ActionResult<StorageConfigEntity> UpdateStorage(int id, [FromBody] UpdateStorageRequest request)
    {
        // if (!StorageItems.TryGetValue(id, out var existing))
        // {
        //     return NotFound();
        // }
        //
        // if (StorageItems.Values.Any(x => x.Id != id && x.Key.Equals(request.Key, StringComparison.OrdinalIgnoreCase)))
        // {
        //     return Conflict($"Storage key '{request.Key}' already exists.");
        // }
        
        // existing.Method = request.Method;
        // existing.Parameters = request.Parameters;
        // existing.Description = request.Description;
        // existing.UpdatedAt = DateTime.UtcNow;
        //
        // return Ok(existing);
        return Ok();
    }

    [HttpGet("list")]
    public ActionResult<IReadOnlyCollection<StorageConfigEntity>> ListStorage()
    {
        // var result = StorageItems.Values
        //     .OrderBy(x => x.Id)
        //     .ToArray();
        //
        // return Ok(result);
        return Ok();
    }

    [HttpDelete("delete/{id:int}")]
    public IActionResult DeleteStorage(int id)
    {
        // if (!StorageItems.TryRemove(id, out _))
        // {
        //     return NotFound();
        // }
        //
        // return NoContent();
        return NoContent();
    }

    [HttpGet("{id:int}")]
    public ActionResult<StorageConfigEntity> GetStorage(int id)
    {
        // if (!StorageItems.TryGetValue(id, out var item))
        // {
        //     return NotFound();
        // }
        //
        // return Ok(item);
        return Ok(null);
    }
}

public sealed class CreateStorageRequest
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }

    [Required]
    public StorageMethod Method { get; init; }

    [Required]
    public required string Parameters { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }
}

public sealed class UpdateStorageRequest
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }

    [Required]
    public StorageMethod Method { get; init; }

    [Required]
    public required string Parameters { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }
}
