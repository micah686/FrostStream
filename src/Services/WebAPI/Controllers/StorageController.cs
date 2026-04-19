using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Shared;

namespace WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StorageController : ControllerBase
{
    // Temporary in-memory data store until integrated with DataBridge.
    private static readonly ConcurrentDictionary<int, StorageConfigEntity> StorageItems = new();
    private static int _nextId = 2;

    static StorageController()
    {
        StorageItems.TryAdd(1, new StorageConfigEntity
        {
            Id = 1,
            Key = "default",
            Method = StorageMethod.PosixLocal,
            Parameters = "{\"path\":\"./data/\"}",
            Description = "Fallback/Default Local Storage",
            CreatedAt = DateTime.UtcNow
        });
    }

    [HttpPost("create")]
    public ActionResult<StorageConfigEntity> CreateStorage([FromBody] CreateStorageRequest request)
    {
        var id = Interlocked.Increment(ref _nextId);
        var now = DateTime.UtcNow;

        var entity = new StorageConfigEntity
        {
            Id = id,
            Key = request.Key,
            Method = request.Method,
            Parameters = request.Parameters,
            Description = request.Description,
            CreatedAt = now,
            UpdatedAt = null
        };

        if (StorageItems.Values.Any(x => x.Key.Equals(entity.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict($"Storage key '{entity.Key}' already exists.");
        }

        StorageItems.TryAdd(id, entity);
        return CreatedAtAction(nameof(GetStorage), new { id }, entity);
    }

    [HttpPut("update/{id:int}")]
    public ActionResult<StorageConfigEntity> UpdateStorage(int id, [FromBody] UpdateStorageRequest request)
    {
        if (!StorageItems.TryGetValue(id, out var existing))
        {
            return NotFound();
        }

        if (StorageItems.Values.Any(x => x.Id != id && x.Key.Equals(request.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict($"Storage key '{request.Key}' already exists.");
        }

        existing.Key = request.Key;
        existing.Method = request.Method;
        existing.Parameters = request.Parameters;
        existing.Description = request.Description;
        existing.UpdatedAt = DateTime.UtcNow;

        return Ok(existing);
    }

    [HttpGet("list")]
    public ActionResult<IReadOnlyCollection<StorageConfigEntity>> ListStorage()
    {
        var result = StorageItems.Values
            .OrderBy(x => x.Id)
            .ToArray();

        return Ok(result);
    }

    [HttpDelete("delete/{id:int}")]
    public IActionResult DeleteStorage(int id)
    {
        if (!StorageItems.TryRemove(id, out _))
        {
            return NotFound();
        }

        return NoContent();
    }

    [HttpGet("{id:int}")]
    public ActionResult<StorageConfigEntity> GetStorage(int id)
    {
        if (!StorageItems.TryGetValue(id, out var item))
        {
            return NotFound();
        }

        return Ok(item);
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
