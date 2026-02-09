using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 1. Serve the raw video file
app.MapGet("/content/video.mp4", async (HttpContext ctx) =>
{
    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "video.mp4");
    if (!File.Exists(filePath)) return Results.NotFound("video.mp4 not found");
    return Results.File(filePath, "video/mp4", enableRangeProcessing: true);
});


// 2. Serve the "Watch" page with embedded JSON-LD
app.MapGet("/watch", async (HttpContext ctx) =>
{
    var jsonPath = Path.Combine(Directory.GetCurrentDirectory(), "video.info.json");
    if (!File.Exists(jsonPath)) return Results.NotFound("video.info.json not found");

    // Read the original JSON
    var jsonString = await File.ReadAllTextAsync(jsonPath);
    var node = JsonNode.Parse(jsonString);

    // CRITICAL: We must override the 'url' field in the JSON.
    // If we leave the original YouTube URL, yt-dlp might ignore our local file 
    // and try to hit YouTube directly. We point it to our local endpoint.
    var request = ctx.Request;
    var localVideoUrl = $"{request.Scheme}://{request.Host}/content/video.mp4";
    
    if (node is JsonObject obj)
    {
        obj["url"] = localVideoUrl;
        
        // Also standardizing type to 'VideoObject' ensures schema.org compliance
        // though yt-dlp is usually permissive.
        obj["@context"] = "https://schema.org";
        obj["@type"] = "VideoObject";
        
        // Ensure contentUrl is set to local video as well
        obj["contentUrl"] = localVideoUrl;
        
        // If the JSON has a 'formats' list, yt-dlp might prefer those HTTP links.
        // For a simple test, we can clear formats to force it to use 'url' or 'contentUrl'.
        if (obj.ContainsKey("formats"))
        {
            obj.Remove("formats");
        }
    }

    var modifiedJson = node?.ToString();

    var html = $@"
    <!DOCTYPE html>
    <html>
    <head>
        <title>Local Test</title>
        <script type=""application/ld+json"">
            {modifiedJson}
        </script>
        
        <meta property=""og:type"" content=""video.other"" />
        <meta property=""og:video"" content=""{localVideoUrl}"" />
        <meta property=""og:video:url"" content=""{localVideoUrl}"" />
    </head>
    <body>
        <h1>Mirroring: {node?["title"]}</h1>
    </body>
    </html>";

    return Results.Content(html, "text/html");
});

app.Run();

//###################################################################################################################
// Point yt-dlp at http://localhost:5000/watch
//like so: yt-dlp --write-info-json http://localhost:5000/watch
//##################################################################################################################
