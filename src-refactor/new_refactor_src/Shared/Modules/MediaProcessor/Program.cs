using Microsoft.Extensions.Hosting;

namespace MediaProcessor;

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
