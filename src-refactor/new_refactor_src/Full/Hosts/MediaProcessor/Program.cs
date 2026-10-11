using Microsoft.Extensions.Hosting;

using MediaProcessor;

namespace FrostStream.Full.MediaProcessor;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();
        builder.AddMediaProcessorModule();
        var app = builder.Build();
        await app.RunAsync();
    }
}
