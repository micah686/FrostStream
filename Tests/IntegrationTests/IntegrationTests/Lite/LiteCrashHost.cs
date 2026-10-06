using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WebAPI;

namespace IntegrationTests.Lite;

/// <summary>Launches the same fixture host in a child process so the test can terminate it
/// without StopAsync, disposal, acknowledgements, or workflow cleanup.</summary>
internal static class LiteCrashHost
{
    // The custom entry point only adds the crash child; normal invocations use the generated runner.
#pragma warning disable TUnit0034
    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] != "--lite-crash-host")
            return await MicrosoftTestingPlatformApplication.RunAsync(arguments);
        await RunAsync(arguments[1], arguments[2], int.Parse(arguments[3], System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    }

#pragma warning restore TUnit0034

    private static async Task RunAsync(string directory, string typesenseUrl, int forbiddenPort)
    {
        var builder = LiteRuntimeFixture.Builder(directory, typesenseUrl, forbiddenPort);
        var downloader = LiteRuntimeFixture.CreateDownloader(Path.Combine(directory, "fixture.mp4"), async (_, token) =>
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "acquisition-started"), "running", token);
            await Task.Delay(Timeout.Infinite, token);
        });
        builder.Services.Replace(ServiceDescriptor.Singleton(downloader));
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapWebAPIModule();
        await app.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "child-address"), app.Urls.Single());
        await app.WaitForShutdownAsync();
    }
}
