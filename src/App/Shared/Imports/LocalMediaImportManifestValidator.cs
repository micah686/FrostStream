namespace Shared.Imports;

public static class LocalMediaImportManifestValidator
{
    public static LocalMediaImportManifestValidationResult Validate(LocalMediaImportManifest? manifest)
    {
        var errors = new List<string>();
        if (manifest is null)
        {
            return new LocalMediaImportManifestValidationResult(false, ["Manifest is required."]);
        }

        if (manifest.Items.Count == 0)
        {
            errors.Add("Manifest must contain at least one item.");
        }

        for (var i = 0; i < manifest.Items.Count; i++)
        {
            var item = manifest.Items[i];
            if (!LocalImportPathRules.TryNormalizeRelativePath(item.File, out _, out var itemError))
            {
                errors.Add($"items[{i}].file: {itemError}");
            }

            if (item.Sidecars?.InfoJson is { } infoJson &&
                !LocalImportPathRules.TryNormalizeRelativePath(infoJson, out _, out var infoJsonError))
            {
                errors.Add($"items[{i}].sidecars.infoJson: {infoJsonError}");
            }

            if (item.Sidecars?.Thumbnail is { } thumbnail &&
                !LocalImportPathRules.TryNormalizeRelativePath(thumbnail, out _, out var thumbnailError))
            {
                errors.Add($"items[{i}].sidecars.thumbnail: {thumbnailError}");
            }

            if (item.Sidecars?.Captions is { Count: > 0 } captions)
            {
                for (var c = 0; c < captions.Count; c++)
                {
                    if (!LocalImportPathRules.TryNormalizeRelativePath(captions[c].File, out _, out var captionError))
                    {
                        errors.Add($"items[{i}].sidecars.captions[{c}].file: {captionError}");
                    }
                }
            }
        }

        return new LocalMediaImportManifestValidationResult(errors.Count == 0, errors);
    }
}

public sealed record LocalMediaImportManifestValidationResult(bool IsValid, IReadOnlyList<string> Errors);
