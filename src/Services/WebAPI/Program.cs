using FlySwattr.NATS.Extensions;
using FlySwattr.NATS.Topology.Extensions;
using Microsoft.EntityFrameworkCore;
using Shared.Storage;
using Shared.Topology;
using WebAPI.Endpoints;
using WebAPI.HealthChecks;
using WebAPI.Middleware;

namespace WebAPI;

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

        // Register storage config client for health checks
        builder.Services.AddSingleton<IStorageConfigClient, NatsStorageConfigClient>();

        // Add enhanced health checks including storage
        builder.Services.AddHealthChecks()
            .AddCheck<StorageHealthCheck>("storage", tags: ["ready", "storage"]);

        builder.Services.AddNatsTopologySource<JobsTopology>();

        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();

        // Add correlation ID middleware for distributed tracing
        app.UseCorrelationId();

        app.UseAuthorization();

        app.MapFileEndpoints();
        app.MapDefaultEndpoints();

        app.Run();
    }
}
