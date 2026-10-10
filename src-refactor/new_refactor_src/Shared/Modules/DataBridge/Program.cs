using Microsoft.Extensions.Hosting;

namespace DataBridge;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();
        builder.AddDataBridgeModule();
        var app = builder.Build();
        app.InitializeDataBridge();
        await app.RunAsync();
    }
}
