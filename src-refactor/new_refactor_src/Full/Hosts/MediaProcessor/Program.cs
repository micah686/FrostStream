using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FrostStream.Full.Infrastructure;

using MediaProcessor;

namespace FrostStream.Full.MediaProcessor;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();
        builder.AddFullMessaging();
        builder.Services.AddHttpClient<global::MediaProcessor.Storage.MediaProcessorStorageClient>();
        builder.Services.AddTransient<global::MediaProcessor.Storage.IMediaProcessorStorageClient>(sp => sp.GetRequiredService<global::MediaProcessor.Storage.MediaProcessorStorageClient>());
        builder.AddMediaProcessorModule();
        var app = builder.Build();
        await app.RunAsync();
    }
}
