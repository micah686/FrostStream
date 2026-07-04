namespace Shared.Imports;

/// <summary>
/// Conventions for the static "incoming" folder that local media imports read from.
///
/// The folder location is operator-configured (not chosen per-import by the admin UI). A
/// worker creates it at startup and drops a <see cref="ManifestTemplateFileName"/> next to
/// where it expects the admin to place a <see cref="ManifestFileName"/> plus the media files.
/// </summary>
public static class LocalImportIncoming
{
    /// <summary>Default folder name for the incoming import root.</summary>
    public const string FolderName = "incoming";

    /// <summary>File the import reads to discover which files to import.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>Scaffolded example manifest placed alongside <see cref="ManifestFileName"/>.</summary>
    public const string ManifestTemplateFileName = "manifest.json.template";

    /// <summary>
    /// Marker stored in the batch/item <c>SourceRoot</c> column. The real absolute path is
    /// worker-local (the configured incoming root), so persistence records the logical name.
    /// </summary>
    public const string SourceRootMarker = "incoming";

    /// <summary>Example manifest contents written to <see cref="ManifestTemplateFileName"/>.</summary>
    public const string ManifestTemplateContent = """
        {
          "items": [
            {
              "file": "channel/video-01.mkv",
              "provider": "youtube",
              "sourceMediaId": "dQw4w9WgXcQ",
              "sourceUrl": "https://youtube.com/watch?v=dQw4w9WgXcQ",
              "title": "My archived video",
              "sidecars": {
                "infoJson": "channel/video-01.info.json",
                "thumbnail": "channel/video-01.jpg",
                "captions": [
                  { "file": "channel/video-01.en.vtt", "languageCode": "en", "captionType": "manual" }
                ]
              }
            }
          ]
        }
        """;

    /// <summary>
    /// Creates the incoming folder if missing and writes the manifest template when it is not
    /// already present. Never overwrites an existing template so operators can customise it.
    /// </summary>
    public static void EnsureScaffold(string incomingRoot)
    {
        if (string.IsNullOrWhiteSpace(incomingRoot))
            throw new ArgumentException("Incoming root is required.", nameof(incomingRoot));

        Directory.CreateDirectory(incomingRoot);

        var templatePath = Path.Combine(incomingRoot, ManifestTemplateFileName);
        if (!File.Exists(templatePath))
            File.WriteAllText(templatePath, ManifestTemplateContent);
    }
}
