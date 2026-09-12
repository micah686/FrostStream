using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DataBridge.Data;
using Microsoft.AspNetCore.Mvc;
using Shared.Database;

namespace FrostStream.Lite.Controllers;

[ApiController]
[Route("api/user/option-presets")]
public sealed class OptionPresetsController(IOptionPresetsRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => Ok((await repository.ListAsync(cancellationToken)).Select(Map));

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, CancellationToken cancellationToken)
    {
        var entity = await repository.GetByKeyAsync(key, cancellationToken);
        return entity is null ? NotFound() : Ok(Map(entity));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] OptionPresetWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (await repository.GetByKeyAsync(request.Key, cancellationToken) is not null)
            return Conflict($"Preset key '{request.Key}' already exists.");

        var entity = await repository.CreateAsync(
            request.Key,
            request.Name,
            request.Description,
            request.YtDlpOptions.GetRawText(),
            cancellationToken);
        return CreatedAtAction(nameof(Get), new { key = entity.Key }, Map(entity));
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Update(
        string key,
        [FromBody] OptionPresetUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var entity = await repository.UpdateAsync(
            key,
            request.Name,
            request.Description,
            request.YtDlpOptions.GetRawText(),
            cancellationToken);
        return entity is null ? NotFound() : Ok(Map(entity));
    }

    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
        => await repository.DeleteAsync(key, cancellationToken) ? NoContent() : NotFound();

    private static object Map(OptionPresetEntity entity)
    {
        using var document = JsonDocument.Parse(entity.YtDlpOptionsJson);
        return new
        {
            entity.Id,
            entity.Key,
            entity.Name,
            entity.Description,
            ytDlpOptions = document.RootElement.Clone(),
            entity.CreatedAt,
            entity.LastUpdated
        };
    }
}

public sealed class OptionPresetWriteRequest
{
    [Required, StringLength(100, MinimumLength = 2), RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }
    [Required, StringLength(255, MinimumLength = 1)]
    public required string Name { get; init; }
    [StringLength(2000)] public string? Description { get; init; }
    [Required] public required JsonElement YtDlpOptions { get; init; }
}

public sealed class OptionPresetUpdateRequest
{
    [Required, StringLength(255, MinimumLength = 1)]
    public required string Name { get; init; }
    [StringLength(2000)] public string? Description { get; init; }
    [Required] public required JsonElement YtDlpOptions { get; init; }
}
