using Microsoft.Extensions.Hosting;

using WebAPI;

namespace FrostStream.Full.WebAPI;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.AddWebAPIModule();
        var app = builder.Build();
        app.MapWebAPIModule();
        app.MapDefaultEndpoints();
        await app.RunAsync();
    }
}
