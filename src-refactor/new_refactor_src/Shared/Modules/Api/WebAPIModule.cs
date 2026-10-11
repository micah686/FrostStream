using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Scalar.AspNetCore;
using Shared.Auth;
using Shared.Backups;
using Shared.LiveChat;
using Shared.Secrets;
using Shared.Storage;
using System.Text.Json.Serialization;
using WebAPI.Auth;
using WebAPI.Features.BackgroundJobs;
using WebAPI.Features.Backups;
using WebAPI.Features.Downloads;
using WebAPI.Features.Media;
using WebAPI.Features.Media.Casting;

using Shared.Deployment;
using Microsoft.Extensions.Hosting;

namespace WebAPI;

public static class WebAPIModule
{
    public static IHostApplicationBuilder AddWebAPIModule(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(WebAPIModule))) return builder;
        builder.AddDeployment();

        // Fail fast if the [Endpoint] attributes have drifted from the registry (the Finding-1 class
        // of bug). This is the single drift guard between attributes, the catalog, and seeding.
        EndpointCatalogValidator.ValidateSharedEndpoints(typeof(WebAPIModule).Assembly);

        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, CastTokenAuthenticationHandler>(CastTokenDefaults.Scheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, PodcastTokenAuthenticationHandler>(PodcastTokenDefaults.Scheme, _ => { });
        builder.Services.Configure<LiveChatOptions>(builder.Configuration.GetSection(LiveChatOptions.SectionName));
        builder.Services.Configure<CastTokenOptions>(builder.Configuration.GetSection(CastTokenOptions.SectionName));
        builder.Services.Configure<PodcastTokenOptions>(builder.Configuration.GetSection(PodcastTokenOptions.SectionName));
        builder.Services.AddSingleton<CastTokenService>();
        builder.Services.AddSingleton<PodcastTokenService>();
        builder.Services.Configure<CastingOptions>(builder.Configuration.GetSection(CastingOptions.SectionName));
        builder.Services.AddSingleton<CastMediaUrlBuilder>();
        builder.Services.AddSingleton<ICastProtocol, ChromecastCastProtocol>();
        builder.Services.AddSingleton<ICastProtocol, FCastCastProtocol>();
        builder.Services.AddSingleton<ICastDeviceRegistry, CastDeviceRegistry>();
        builder.Services.AddSingleton<CastSessionManager>();
        builder.Services.AddScoped<AudioRenditionResolver>();
        builder.Services.AddScoped<ChannelAudioResolver>();
        builder.Services.AddScoped<StreamRenditionResolver>();
        builder.Services.AddCors(options => options.AddPolicy(MediaCors.Policy, policy =>
            policy.AllowAnyOrigin().AllowAnyHeader().WithMethods("GET", "HEAD")));

        builder.Services.AddControllers()
            .AddApplicationPart(typeof(WebAPIModule).Assembly)
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
        builder.Services.AddSingleton<BackupJobService>();
        builder.Services.AddSingleton<DownloadQueueHub>();
        builder.Services.AddHostedService<DownloadQueueHub>(p => p.GetRequiredService<DownloadQueueHub>());
        builder.Services.AddSingleton<RenditionProgressHub>();
        builder.Services.AddHostedService<RenditionProgressHub>(p => p.GetRequiredService<RenditionProgressHub>());
        builder.Services.AddSingleton<BackgroundJobHub>();
        builder.Services.AddHostedService<BackgroundJobHub>(p => p.GetRequiredService<BackgroundJobHub>());
        builder.Services.AddFrostStreamStorage();

        //var natsUrl = builder.Configuration["NATS:Url"] ?? "nats://localhost:24040";


        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        return builder;
    }

    public static WebApplication MapWebAPIModule(this WebApplication app)
    {




        // Configure the HTTP request pipeline.
        // if (app.Environment.IsDevelopment())
        // {
        var exposeOpenApi = app.Environment.IsDevelopment() ||
            app.Configuration.GetValue("Auth:ExposeOpenApi", false);
        if (exposeOpenApi)
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference().AllowAnonymous();
        }
        //}


        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/auth") ||
                context.Request.Path.StartsWithSegments("/api/auth"))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.Pragma = "no-cache";
                    return Task.CompletedTask;
                });
            }

            await next();
        });

        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex) when (context.Request.Path.StartsWithSegments("/api/global/backups"))
            {
                app.Logger.LogError(ex, "BackupService request failed.");
                context.Response.StatusCode = ex switch
                {
                    NotSupportedException or ArgumentException => StatusCodes.Status400BadRequest,
                    FileNotFoundException => StatusCodes.Status404NotFound,
                    InvalidDataException => StatusCodes.Status422UnprocessableEntity,
                    _ => StatusCodes.Status503ServiceUnavailable
                };
                await context.Response.WriteAsJsonAsync(new
                {
                    error = ex is NotSupportedException or ArgumentException or FileNotFoundException or InvalidDataException or LocalBackupUnavailableException
                        ? ex.Message
                        : "The backup service is unavailable. Check its health and configured backup directory."
                });
            }
        });

        if (Uri.TryCreate(app.Configuration["Auth:PublicOrigin"], UriKind.Absolute, out var configuredPublicOrigin) &&
            configuredPublicOrigin.Scheme == Uri.UriSchemeHttps)
        {
            app.UseHttpsRedirection();
        }
        app.UseCors();
        app.UseAuthentication();
        foreach (var pipeline in app.Services.GetServices<IApiSecurityPipeline>()) pipeline.Configure(app);
        app.UseAuthorization();
        app.MapControllers();


        return app;
    }


}
