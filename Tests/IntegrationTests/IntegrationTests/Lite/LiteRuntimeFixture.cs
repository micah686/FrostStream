using System.Diagnostics;
using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataBridge.Data;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Lite;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using NSubstitute;
using Shared.Storage;
using Shouldly;
using WebAPI;
using YtDlpSharpLib;
using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;
using YtDlpSharpLib.Progress;
using YtDlpSharpLib.Provisioning;

namespace IntegrationTests.Lite;

/// <summary>The production five-module host, with real SQLite, Typesense, storage, and FFmpeg.
/// Only Internet acquisition and binary provisioning are replaced with deterministic inputs.</summary>
internal sealed class LiteRuntimeFixture : IAsyncDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"froststream-lite-e2e-{Guid.NewGuid():N}");
    public string Incoming => Path.Combine(DirectoryPath, "incoming");
    public string Video => Path.Combine(DirectoryPath, "fixture.mp4");
    public string TypesenseUrl { get; private set; } = "";
    private IContainer? typesense;
    private readonly TcpListener removedInfrastructure = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource monitorStop = new();
    private Task? monitor;
    private int attempts;
    public int ForbiddenPort => ((IPEndPoint)removedInfrastructure.LocalEndpoint).Port;
    public int RemovedInfrastructureConnections => Volatile.Read(ref attempts);
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public IYtDlpClient Downloader { get; private set; } = null!;
    public Func<string, CancellationToken, Task>? BeforeDownload { get; set; }
    public List<Guid> Jobs { get; } = [];
    public static JsonSerializerOptions Json { get; } = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        json.Converters.Add(new JsonStringEnumConverter());
        return json;
    }

    public async Task StartAsync(CancellationToken token)
    {
        removedInfrastructure.Start();
        monitor = MonitorConnectionsAsync();
        Directory.CreateDirectory(Incoming);
        await CreateVideoAsync(Video, token);
        typesense = new ContainerBuilder("typesense/typesense:30.1")
            .WithCommand("--data-dir=/tmp", "--api-key=lite-test")
            .WithPortBinding(8108, true).Build();
        await typesense.StartAsync(token);
        TypesenseUrl = $"http://{typesense.Hostname}:{typesense.GetMappedPublicPort(8108)}";
        await StartHostAsync(token);
        await SendAsync(HttpMethod.Post, "/api/storage/local/create", new
        {
            key = "e2e-storage", description = "Lite integration fixture", protocol = "Local", path = Path.Combine(DirectoryPath, "storage")
        }, token);
    }

    internal static WebApplicationBuilder Builder(string directory, string typesenseUrl, int forbiddenPort = 1)
    {
        Quartz.Logging.LogContext.SetCurrentLogProvider(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        // Useful on a failure, without leaking credentials or depending on external infrastructure.
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration["Persistence:Sqlite:Path"] = Path.Combine(directory, "application.db");
        builder.Configuration["Worker:IncomingRoot"] = Path.Combine(directory, "incoming");
        builder.Configuration["Typesense:Url"] = typesenseUrl;
        builder.Configuration["Typesense:ApiKey"] = "lite-test";
        builder.Configuration["MediaProcessor:FfmpegPath"] = "ffmpeg";
        builder.Configuration["MediaProcessor:FfprobePath"] = "ffprobe";
        builder.Configuration["MediaProcessor:TempRoot"] = Path.Combine(directory, "processing");
        // Invalid removed-service addresses would fail immediately if accidentally selected.
        builder.Configuration["ConnectionStrings:nats"] = $"nats://127.0.0.1:{forbiddenPort}";
        builder.Configuration["ConnectionStrings:froststreamdb"] = $"Host=127.0.0.1;Port={forbiddenPort};Database=unused";
        builder.Configuration["OpenBao:Address"] = $"http://127.0.0.1:{forbiddenPort}";
        builder.Configuration["BackupService:BaseUrl"] = $"http://127.0.0.1:{forbiddenPort}";
        builder.Configuration["Auth:Authority"] = $"http://127.0.0.1:{forbiddenPort}";
        builder.Configuration["OpenFga:Endpoint"] = $"http://127.0.0.1:{forbiddenPort}";
        builder.ConfigureLiteHost();
        builder.AddServiceDefaults();
        builder.AddLiteModules();
        var binaries = Substitute.For<IYtDlpBinaryDownloader>();
        binaries.DownloadAllAsync(Arg.Any<BinaryDownloadOptions?>(), Arg.Any<IProgress<BinaryDownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new BinaryDownloadResult());
        builder.Services.Replace(ServiceDescriptor.Singleton(binaries));
        return builder;
    }

    private async Task StartHostAsync(CancellationToken token)
    {
        var builder = Builder(DirectoryPath, TypesenseUrl, ((IPEndPoint)removedInfrastructure.LocalEndpoint).Port);
        Downloader = CreateDownloader(Video, (url, cancellation) => BeforeDownload?.Invoke(url, cancellation) ?? Task.CompletedTask);
        builder.Services.Replace(ServiceDescriptor.Singleton(Downloader));
        App = builder.Build();
        App.Urls.Add("http://127.0.0.1:0");
        App.MapWebAPIModule();
        App.MapGet("/api/e2e/new-feature", (Microsoft.AspNetCore.Http.HttpContext context) => context.User.Identity!.Name)
            .RequireAuthorization("future-policy-without-permission-metadata");
        App.MapHealthChecks("/health").AllowAnonymous();
        await App.StartAsync(token);
        Client = new HttpClient { BaseAddress = new Uri(App.Urls.Single()) };
    }

    internal static IYtDlpClient CreateDownloader(string video, Func<string, CancellationToken, Task> beforeDownload)
    {
        var downloader = Substitute.For<IYtDlpClient>();
        downloader.TryGetVideoInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<YtDlpOptions?>())
            .Returns(call => RunResult<VideoInfo>.Succeeded(Metadata(call.ArgAt<string>(0))));
        downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DownloadOptions?>(), Arg.Any<IProgress<YtDlpProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var url = call.ArgAt<string>(0);
                var cancellation = call.ArgAt<CancellationToken>(4);
                await beforeDownload(url, cancellation);
                var output = call.ArgAt<string>(1);
                Directory.CreateDirectory(output);
                File.Copy(video, Path.Combine(output, "media.mp4"), overwrite: true);
                await File.WriteAllTextAsync(Path.Combine(output, "media.info.json"), JsonSerializer.Serialize(Metadata(url)), cancellation);
                call.ArgAt<IProgress<YtDlpProgress>?>(3)?.Report(new YtDlpProgress
                    { Phase = ProgressPhase.Completed, Percent = 100, Message = "Fixture acquisition completed" });
            });
        return downloader;
    }

    private async Task MonitorConnectionsAsync()
    {
        try
        {
            while (!monitorStop.IsCancellationRequested)
            {
                using var connection = await removedInfrastructure.AcceptTcpClientAsync(monitorStop.Token);
                Interlocked.Increment(ref attempts);
            }
        }
        catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
    }

    internal static VideoInfo Metadata(string url) => new()
    {
        Id = new Uri(url).Segments.Last(), Extractor = "generic", Title = "Lite deterministic video",
        WebpageUrl = url, Duration = 2, Extension = "mp4",
        Uploader = "Lite integration", UploaderId = "lite-integration"
    };

    public async Task RestartAsync(CancellationToken token)
    {
        await StopHostAsync(token);
        await ResumeHostAsync(token);
    }

    public async Task StopHostAsync(CancellationToken token)
    {
        Client.Dispose();
        await App.StopAsync(token);
        await App.DisposeAsync();
        App = null!;
    }
    public Task ResumeHostAsync(CancellationToken token) => StartHostAsync(token);

    public async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        using var response = await Client.GetAsync(path, token);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(token));
        return (await response.Content.ReadFromJsonAsync<T>(Json, token))!;
    }

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
        using var response = await Client.SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        response.IsSuccessStatusCode.ShouldBeTrue($"{method} {path}: {(int)response.StatusCode} {text}");
        return string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<JsonElement>(text, Json);
    }

    public async Task<Guid> DownloadAsync(string id, CancellationToken token)
    {
        var result = await SendAsync(HttpMethod.Post, "/api/downloads/video", new { sourceUrl = $"https://fixture.example.test/{id}", storageKey = "e2e-storage" }, token);
        var jobId = result.GetProperty("jobId").GetGuid();
        Jobs.Add(jobId);
        return jobId;
    }

    public async Task<T> DatabaseAsync<T>(Func<DataBridgeDbContext, Task<T>> read)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>());
    }

    public static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition()) await Task.Delay(50, token);
    }

    internal static async Task CreateVideoAsync(string path, CancellationToken token)
    {
        if (File.Exists(path)) return;
        using var process = new Process { StartInfo = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true } };
        foreach (var argument in new[] { "-y", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", path })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(token);
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        process.ExitCode.ShouldBe(0, await stderr);
        await stdout;
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null) { await App.StopAsync(); await App.DisposeAsync(); }
        Quartz.Logging.LogContext.SetCurrentLogProvider(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        if (typesense is not null) await typesense.DisposeAsync();
        monitorStop.Cancel();
        if (monitor is not null) await monitor;
        removedInfrastructure.Stop();
        monitorStop.Dispose();
        foreach (var id in Jobs)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "froststream", "downloads", id.ToString("N"));
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}
