using Microsoft.Extensions.Hosting;

namespace WebAPI;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.AddWebAPIModule();
        var app = builder.Build();
        app.MapWebAPIModule();
        await app.RunAsync();
    }
}
