using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

// Large uploads (videos). Set to null for unlimited, or set to a safe max (e.g. 50GB).
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = null;
});

builder.Services.AddRouting(o => o.LowercaseUrls = true);

var app = builder.Build();

var cfg = app.Configuration.GetSection("VideoHost");
var configuredRoot = cfg.GetValue<string>("RootPath") ?? "data";
var baseUrl = cfg.GetValue<string>("BaseUrl") ?? "http://localhost:5099";

// Resolve RootPath relative to the project content root (typically the csproj folder)
var rootPath = Path.GetFullPath(
    Path.IsPathRooted(configuredRoot)
        ? configuredRoot
        : Path.Combine(app.Environment.ContentRootPath, configuredRoot)
);

app.Logger.LogInformation("VideoHost RootPath resolved to: {RootPath}", rootPath);

Directory.CreateDirectory(Path.Combine(rootPath, "videos"));

var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings.TryAdd(".m3u8", "application/vnd.apple.mpegurl");
contentTypeProvider.Mappings.TryAdd(".ts", "video/mp2t");

// ---------------- Helpers ----------------
static bool IsSafeId(string id)
{
    if (string.IsNullOrWhiteSpace(id)) return false;
    foreach (var ch in id)
    {
        if (!(char.IsLetterOrDigit(ch) || ch is '-' or '_')) return false;
    }
    return true;
}

string VideoDir(string id) => Path.Combine(rootPath, "videos", id);
string InfoJsonPath(string id) => Path.Combine(VideoDir(id), $"{id}.info.json");
string FilesDir(string id) => Path.Combine(VideoDir(id), "files");
string HlsDir(string id) => Path.Combine(VideoDir(id), "hls");

static string CombineUrl(string baseUrl, string path)
{
    baseUrl = baseUrl.TrimEnd('/');
    path = path.StartsWith('/') ? path : "/" + path;
    return baseUrl + path;
}

static bool TryGetContentType(FileExtensionContentTypeProvider provider, string path, out string contentType)
{
    if (provider.TryGetContentType(path, out contentType!)) return true;
    contentType = "application/octet-stream";
    return false;
}

static string? GuessHeightFromName(string fileName)
{
    var lower = fileName.ToLowerInvariant();
    var idx = lower.LastIndexOf("p.");
    if (idx > 0)
    {
        var start = idx - 1;
        while (start >= 0 && char.IsDigit(lower[start])) start--;
        start++;
        var digits = lower.Substring(start, idx - start);
        if (digits.Length > 0) return digits;
    }
    return null;
}

static async Task<(string? title, string? description, string? thumbnail)> ReadSomeInfoFieldsAsync(string infoPath)
{
    if (!File.Exists(infoPath)) return (null, null, null);

    await using var fs = File.OpenRead(infoPath);
    using var doc = await JsonDocument.ParseAsync(fs);

    string? GetString(params string[] path)
    {
        JsonElement cur = doc.RootElement;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            if (!cur.TryGetProperty(p, out cur)) return null;
        }
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
    }

    var title = GetString("title");
    var desc = GetString("description");
    var thumb = GetString("thumbnail");

    if (thumb is null && doc.RootElement.TryGetProperty("thumbnails", out var thumbs) &&
        thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0)
    {
        var first = thumbs[0];
        if (first.ValueKind == JsonValueKind.Object &&
            first.TryGetProperty("url", out var url) &&
            url.ValueKind == JsonValueKind.String)
        {
            thumb = url.GetString();
        }
    }

    return (title, desc, thumb);
}

// ---------------- Routes ----------------

// Health
app.MapGet("/health", () => Results.Ok(new { ok = true }));

// List available IDs (for UI/testing)
app.MapGet("/api/videos", () =>
{
    var videosRoot = Path.Combine(rootPath, "videos");
    if (!Directory.Exists(videosRoot)) return Results.Ok(Array.Empty<string>());

    var ids = Directory.EnumerateDirectories(videosRoot)
        .Select(Path.GetFileName)
        .Where(x => x is not null)
        .OrderBy(x => x)
        .ToArray();

    return Results.Ok(ids);
});

// IMPORT: upload video file(s) + optional info.json into /videos/{id}/...
// multipart fields:
// - files: one or more video files (recommended name includes 720p, 360p, etc.)
// - infoJson: optional .info.json file (any name allowed; stored as {id}.info.json)
// query params:
// - overwrite=true|false (default false)
app.MapPost("/api/videos/{id}/import", async (HttpRequest request, string id) =>
{
    if (!IsSafeId(id)) return Results.BadRequest(new { error = "Invalid id" });
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data" });

    var overwrite = string.Equals(request.Query["overwrite"], "true", StringComparison.OrdinalIgnoreCase);

    var form = await request.ReadFormAsync();

    var fileUploads = form.Files.Where(f => string.Equals(f.Name, "files", StringComparison.OrdinalIgnoreCase)).ToList();
    var infoUpload = form.Files.FirstOrDefault(f => string.Equals(f.Name, "infoJson", StringComparison.OrdinalIgnoreCase));

    if (fileUploads.Count == 0 && infoUpload is null)
        return Results.BadRequest(new { error = "Provide at least one 'files' upload and/or an 'infoJson' upload." });

    var vdir = VideoDir(id);
    var filesDir = FilesDir(id);

    Directory.CreateDirectory(vdir);
    Directory.CreateDirectory(filesDir);

    var saved = new List<object>();

    // Save video files
    foreach (var f in fileUploads)
    {
        var safeName = Path.GetFileName(f.FileName);
        if (string.IsNullOrWhiteSpace(safeName)) continue;

        var destPath = Path.Combine(filesDir, safeName);
        if (File.Exists(destPath) && !overwrite)
            return Results.Conflict(new { error = $"File already exists: {safeName}. Use ?overwrite=true to replace." });

        await using var fs = File.Create(destPath);
        await f.CopyToAsync(fs);

        saved.Add(new
        {
            kind = "file",
            name = safeName,
            bytes = new FileInfo(destPath).Length,
            url = CombineUrl(baseUrl, $"/api/videos/{id}/file/{Uri.EscapeDataString(safeName)}")
        });
    }

    // Save info.json (no model)
    if (infoUpload is not null)
    {
        var infoPath = InfoJsonPath(id);
        if (File.Exists(infoPath) && !overwrite)
            return Results.Conflict(new { error = $"Info JSON already exists for {id}. Use ?overwrite=true to replace." });

        await using var fs = File.Create(infoPath);
        await infoUpload.CopyToAsync(fs);

        saved.Add(new
        {
            kind = "infoJson",
            name = Path.GetFileName(infoUpload.FileName),
            bytes = new FileInfo(infoPath).Length,
            url = CombineUrl(baseUrl, $"/api/videos/{id}/info.json")
        });
    }

    return Results.Ok(new
    {
        id,
        watch = CombineUrl(baseUrl, $"/watch/{id}"),
        saved
    });
});

// Raw info.json download (no model)
app.MapGet("/api/videos/{id}/info.json", (string id) =>
{
    if (!IsSafeId(id)) return Results.BadRequest(new { error = "Invalid id" });

    var path = InfoJsonPath(id);
    if (!File.Exists(path)) return Results.NotFound();

    return Results.File(path, "application/json");
});

// Stream a progressive file
app.MapGet("/api/videos/{id}/file/{fileName}", (string id, string fileName) =>
{
    if (!IsSafeId(id)) return Results.BadRequest(new { error = "Invalid id" });
    fileName = Path.GetFileName(fileName);

    var path = Path.Combine(FilesDir(id), fileName);
    if (!File.Exists(path)) return Results.NotFound();

    TryGetContentType(contentTypeProvider, path, out var ct);
    return Results.File(path, ct, enableRangeProcessing: true);
});

// Serve HLS playlists/segments
app.MapGet("/api/videos/{id}/hls/{**hlsPath}", (string id, string hlsPath) =>
{
    if (!IsSafeId(id)) return Results.BadRequest(new { error = "Invalid id" });

    hlsPath = hlsPath.Replace('\\', '/');
    if (hlsPath.Contains("..", StringComparison.Ordinal)) return Results.BadRequest(new { error = "Invalid path" });

    var path = Path.Combine(HlsDir(id), hlsPath);
    if (!File.Exists(path)) return Results.NotFound();

    TryGetContentType(contentTypeProvider, path, out var ct);
    return Results.File(path, ct, enableRangeProcessing: true);
});

// Formats API (scans /files and optionally parses HLS master playlist)
app.MapGet("/api/videos/{id}/formats", async (string id) =>
{
    if (!IsSafeId(id)) return Results.BadRequest(new { error = "Invalid id" });

    var list = new List<object>();

    // Progressive formats
    var filesDir = FilesDir(id);
    if (Directory.Exists(filesDir))
    {
        foreach (var file in Directory.EnumerateFiles(filesDir))
        {
            var name = Path.GetFileName(file);
            var h = GuessHeightFromName(name);
            int? height = null;
            if (h is not null && int.TryParse(h, out var parsed)) height = parsed;

            list.Add(new
            {
                kind = "file",
                name,
                height,
                url = CombineUrl(baseUrl, $"/api/videos/{id}/file/{Uri.EscapeDataString(name)}")
            });
        }
    }

    // HLS formats
    var masterPath = Path.Combine(HlsDir(id), "master.m3u8");
    if (File.Exists(masterPath))
    {
        var lines = await File.ReadAllLinesAsync(masterPath);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;

            string? uri = null;
            for (int j = i + 1; j < lines.Length; j++)
            {
                var next = lines[j].Trim();
                if (string.IsNullOrWhiteSpace(next)) continue;
                if (next.StartsWith("#")) continue;
                uri = next;
                break;
            }

            int? height = null;
            const string resKey = "RESOLUTION=";
            var resIdx = line.IndexOf(resKey, StringComparison.OrdinalIgnoreCase);
            if (resIdx >= 0)
            {
                var start = resIdx + resKey.Length;
                var end = line.IndexOf(',', start);
                var res = (end < 0 ? line[start..] : line[start..end]).Trim();
                var x = res.IndexOf('x');
                if (x > 0 && int.TryParse(res[(x + 1)..], out var h)) height = h;
            }

            if (uri is not null)
            {
                list.Add(new
                {
                    kind = "hls",
                    height,
                    url = CombineUrl(baseUrl, $"/api/videos/{id}/hls/{Uri.EscapeDataString(uri)}"),
                    master = CombineUrl(baseUrl, $"/api/videos/{id}/hls/master.m3u8")
                });
            }
        }
    }

    return Results.Ok(list);
});

// Watch page that yt-dlp can scrape
app.MapGet("/watch/{id}", async (string id) =>
{
    if (!IsSafeId(id)) return Results.BadRequest("Invalid id");

    var vdir = VideoDir(id);
    if (!Directory.Exists(vdir)) return Results.NotFound("Unknown id");

    var infoPath = InfoJsonPath(id);
    var (title, desc, thumb) = await ReadSomeInfoFieldsAsync(infoPath);

    title ??= id;
    desc ??= $"Local test video {id}";

    // Prefer HLS master if present (best for yt-dlp -F)
    var master = Path.Combine(HlsDir(id), "master.m3u8");
    string? primaryMediaUrl = null;

    if (File.Exists(master))
        primaryMediaUrl = CombineUrl(baseUrl, $"/api/videos/{id}/hls/master.m3u8");
    else
    {
        // fall back to "highest looking" file
        var files = Directory.Exists(FilesDir(id)) ? Directory.EnumerateFiles(FilesDir(id)).ToList() : new List<string>();
        if (files.Count > 0)
        {
            var best = files
                .Select(p => new { path = p, h = GuessHeightFromName(Path.GetFileName(p)) })
                .OrderByDescending(x => x.h is null ? -1 : int.Parse(x.h))
                .First().path;

            var name = Path.GetFileName(best);
            primaryMediaUrl = CombineUrl(baseUrl, $"/api/videos/{id}/file/{Uri.EscapeDataString(name)}");
        }
    }

    var infoUrl = File.Exists(infoPath)
        ? CombineUrl(baseUrl, $"/api/videos/{id}/info.json")
        : null;

    var sb = new StringBuilder();
    sb.AppendLine("<!doctype html>");
    sb.AppendLine("<html><head><meta charset=\"utf-8\"/>");
    sb.AppendLine($"<title>{System.Net.WebUtility.HtmlEncode(title)}</title>");
    sb.AppendLine($"<meta name=\"description\" content=\"{System.Net.WebUtility.HtmlEncode(desc)}\"/>");
    sb.AppendLine($"<meta property=\"og:title\" content=\"{System.Net.WebUtility.HtmlEncode(title)}\"/>");
    sb.AppendLine($"<meta property=\"og:description\" content=\"{System.Net.WebUtility.HtmlEncode(desc)}\"/>");
    if (!string.IsNullOrWhiteSpace(thumb))
        sb.AppendLine($"<meta property=\"og:image\" content=\"{System.Net.WebUtility.HtmlEncode(thumb)}\"/>");
    if (!string.IsNullOrWhiteSpace(primaryMediaUrl))
        sb.AppendLine($"<meta property=\"og:video\" content=\"{System.Net.WebUtility.HtmlEncode(primaryMediaUrl)}\"/>");

    sb.AppendLine("</head><body style=\"font-family: system-ui, sans-serif; max-width: 900px; margin: 40px auto;\">");
    sb.AppendLine($"<h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>");
    sb.AppendLine($"<p><code>{System.Net.WebUtility.HtmlEncode(id)}</code></p>");

    if (!string.IsNullOrWhiteSpace(primaryMediaUrl))
    {
        sb.AppendLine("<p>Primary media:</p>");
        sb.AppendLine($"<p><a href=\"{System.Net.WebUtility.HtmlEncode(primaryMediaUrl)}\">{System.Net.WebUtility.HtmlEncode(primaryMediaUrl)}</a></p>");
    }

    if (infoUrl is not null)
    {
        sb.AppendLine("<p>Info JSON:</p>");
        sb.AppendLine($"<p><a href=\"{System.Net.WebUtility.HtmlEncode(infoUrl)}\">{System.Net.WebUtility.HtmlEncode(infoUrl)}</a></p>");
    }

    sb.AppendLine("<p>Formats:</p>");
    sb.AppendLine($"<p><a href=\"{CombineUrl(baseUrl, $"/api/videos/{id}/formats")}\">/api/videos/{id}/formats</a></p>");
    sb.AppendLine("<p><a href=\"/ui\">Upload/import more</a></p>");

    sb.AppendLine("</body></html>");
    return Results.Content(sb.ToString(), "text/html; charset=utf-8");
});

// ---------------- Simple UI ----------------
app.MapGet("/ui", () =>
{
    var html = """
<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>FrostStream Local Video Host - Import</title>
  <style>
    body { font-family: system-ui, sans-serif; max-width: 920px; margin: 40px auto; padding: 0 16px; }
    input, button { font-size: 14px; padding: 8px; }
    .row { margin: 12px 0; }
    code { background: #f6f6f6; padding: 2px 6px; border-radius: 6px; }
    pre { background: #0b1020; color: #e8e8e8; padding: 12px; border-radius: 10px; overflow: auto; }
    .card { border: 1px solid #ddd; border-radius: 12px; padding: 16px; }
    .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
    .muted { color: #666; }
    progress { width: 100%; height: 18px; }
    a { color: #0a58ca; }
  </style>
</head>
<body>
  <h1>Local Video Import</h1>
  <p class="muted">
    Upload one or more files and (optionally) an <code>.info.json</code>. This will create:
    <code>data/videos/{id}/files/...</code> and <code>data/videos/{id}/{id}.info.json</code>
  </p>

  <div class="grid">
    <div class="card">
      <h2>Import</h2>

      <div class="row">
        <label>Video ID (letters/digits/dash/underscore):</label><br/>
        <input id="vid" placeholder="abc-123" style="width: 100%;" />
      </div>

      <div class="row">
        <label>Video file(s):</label><br/>
        <input id="files" type="file" multiple style="width: 100%;" />
        <div class="muted">Tip: include “720p” in the filename if you want the formats API to guess height.</div>
      </div>

      <div class="row">
        <label>info.json (optional):</label><br/>
        <input id="info" type="file" accept=".json,application/json" style="width: 100%;" />
      </div>

      <div class="row">
        <label><input type="checkbox" id="overwrite" /> overwrite existing</label>
      </div>

      <div class="row">
        <button id="btn">Upload</button>
      </div>

      <div class="row">
        <progress id="prog" value="0" max="100" style="display:none;"></progress>
        <div id="status" class="muted"></div>
      </div>
    </div>

    <div class="card">
      <h2>Existing IDs</h2>
      <div class="row"><button id="refresh">Refresh list</button></div>
      <ul id="list"></ul>
      <div class="muted">Click an id to open its watch page.</div>
    </div>
  </div>

  <h2>Result</h2>
  <pre id="out">(nothing yet)</pre>

<script>
function setStatus(msg) { document.getElementById('status').textContent = msg; }
function setOut(obj) { document.getElementById('out').textContent = typeof obj === 'string' ? obj : JSON.stringify(obj, null, 2); }

async function refreshList() {
  const ul = document.getElementById('list');
  ul.innerHTML = '';
  const r = await fetch('/api/videos');
  const ids = await r.json();
  for (const id of ids) {
    const li = document.createElement('li');
    const a = document.createElement('a');
    a.href = '/watch/' + encodeURIComponent(id);
    a.textContent = id;
    a.target = '_blank';
    li.appendChild(a);
    ul.appendChild(li);
  }
}
document.getElementById('refresh').addEventListener('click', refreshList);
refreshList();

document.getElementById('btn').addEventListener('click', () => {
  const id = document.getElementById('vid').value.trim();
  if (!id) { alert('Enter an id'); return; }

  const files = document.getElementById('files').files;
  const info = document.getElementById('info').files[0] || null;
  const overwrite = document.getElementById('overwrite').checked;

  if ((!files || files.length === 0) && !info) {
    alert('Pick at least one video file and/or an info.json');
    return;
  }

  const fd = new FormData();
  if (files) for (const f of files) fd.append('files', f, f.name);
  if (info) fd.append('infoJson', info, info.name);

  const url = '/api/videos/' + encodeURIComponent(id) + '/import' + (overwrite ? '?overwrite=true' : '');
  const prog = document.getElementById('prog');
  prog.style.display = 'block';
  prog.value = 0;

  setStatus('Uploading...');
  setOut('(uploading...)');

  const xhr = new XMLHttpRequest();
  xhr.open('POST', url);

  xhr.upload.onprogress = (e) => {
    if (e.lengthComputable) {
      prog.value = Math.round((e.loaded / e.total) * 100);
    }
  };

  xhr.onload = async () => {
    prog.style.display = 'none';
    if (xhr.status >= 200 && xhr.status < 300) {
      const obj = JSON.parse(xhr.responseText);
      setOut(obj);
      setStatus('Done.');
      await refreshList();
      if (obj.watch) {
        setStatus('Done. Opening watch page...');
        window.open(obj.watch, '_blank');
      }
    } else {
      setOut(xhr.responseText || ('HTTP ' + xhr.status));
      setStatus('Failed.');
    }
  };

  xhr.onerror = () => {
    prog.style.display = 'none';
    setStatus('Network error.');
  };

  xhr.send(fd);
});
</script>

</body>
</html>
""";
    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();
