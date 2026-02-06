using FlySwattr.NATS.Extensions;
using WebAPI;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        // Add services to the container.
        builder.Services.AddAuthorization();

        builder.Services.AddEnterpriseNATSMessaging(opts =>
        {
            opts.Core.Url = builder.Configuration["NATS:Url"] ?? "nats://localhost:4222";
        });

        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapJobEndpoints();

        app.Run();
    }
}