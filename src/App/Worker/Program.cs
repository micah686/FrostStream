using Microsoft.Extensions.Hosting;

namespace Worker;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();
        builder.AddWorkerModule();
        var app = builder.Build();
        await app.RunAsync();
    }
}
