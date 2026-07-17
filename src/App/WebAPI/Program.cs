using Conduit.NATS;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Scalar.AspNetCore;
using Shared.Auth;
using Shared.Backups;
using Shared.Secrets;
using Shared.Storage;
using System.Text.Json.Serialization;
using WebAPI.Auth;
using WebAPI.Features.Backups;
using WebAPI.Features.Downloads;
using WebAPI.Features.Media;
using WebAPI.Features.Media.Casting;

namespace WebAPI;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        // Fail fast if the [Endpoint] attributes have drifted from the registry (the Finding-1 class
        // of bug). This is the single drift guard between attributes, the catalog, and seeding.
        EndpointCatalogValidator.Validate(typeof(Program).Assembly);

        var singleUserMode = AuthMode.IsSingleUserMode(builder.Configuration);
        var authOptions = builder.Configuration
            .GetSection(FrostStreamAuthOptions.SectionName)
            .Get<FrostStreamAuthOptions>() ?? new FrostStreamAuthOptions();
        WebApiHardening.ValidateStartup(authOptions, singleUserMode, builder.Environment.IsProduction());
        builder.Services.Configure<FrostStreamAuthOptions>(builder.Configuration.GetSection(FrostStreamAuthOptions.SectionName));
        builder.Services.Configure<OpenFgaOptions>(builder.Configuration.GetSection(OpenFgaOptions.SectionName));
        builder.Services.Configure<AuthentikOptions>(builder.Configuration.GetSection(AuthentikOptions.SectionName));

        // Default scheme is a selector: requests carrying a cast token (sessionless cast devices)
        // authenticate through the CastToken scheme; everything else uses the session scheme for
        // the current mode (SingleUser or JwtBearer).
        const string schemeSelector = "FrostStream";
        var sessionScheme = singleUserMode ? AuthConstants.SingleUserScheme : JwtBearerDefaults.AuthenticationScheme;
        var authBuilder = builder.Services
            .AddAuthentication(schemeSelector)
            .AddPolicyScheme(schemeSelector, "FrostStream scheme selector", options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Request.Query.ContainsKey(CastTokenDefaults.QueryParameter)
                        ? CastTokenDefaults.Scheme
                        : sessionScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, CastTokenAuthenticationHandler>(
                CastTokenDefaults.Scheme,
                _ => { });

        builder.Services.Configure<CastTokenOptions>(builder.Configuration.GetSection(CastTokenOptions.SectionName));
        builder.Services.AddSingleton<CastTokenService>();
        builder.Services.Configure<CastingOptions>(builder.Configuration.GetSection(CastingOptions.SectionName));
        builder.Services.AddSingleton<CastMediaUrlBuilder>();
        builder.Services.AddSingleton<ICastProtocol, ChromecastCastProtocol>();
        builder.Services.AddSingleton<ICastProtocol, FCastCastProtocol>();
        builder.Services.AddSingleton<ICastDeviceRegistry, CastDeviceRegistry>();
        builder.Services.AddSingleton<CastSessionManager>();
        builder.Services.AddScoped<MediaAccessChecker>();
        builder.Services.AddScoped<AudioRenditionResolver>();
        builder.Services.AddCors(options => options.AddPolicy(MediaCors.Policy, policy =>
            policy.AllowAnyOrigin().AllowAnyHeader().WithMethods("GET", "HEAD")));

        if (singleUserMode)
        {
            authBuilder
                .AddScheme<AuthenticationSchemeOptions, SingleUserAuthenticationHandler>(
                    AuthConstants.SingleUserScheme,
                    _ => { });
            builder.Services.AddSingleton<IFrostStreamAuthorizer, AllowAllAuthorizer>();
            builder.Services.AddSingleton<IOpenFgaTupleWriter, NullOpenFgaTupleWriter>();
            builder.Services.AddSingleton<IBundleManagementService, NullBundleManagementService>();
            builder.Services.AddSingleton<IDirectoryService, NullDirectoryService>();
        }
        else
        {
            var authority = builder.Configuration["Auth:Authority"];
            if (string.IsNullOrWhiteSpace(authority))
            {
                throw new InvalidOperationException("Auth:Authority must be configured when SINGLE_USER_MODE is not enabled.");
            }

            var audience = builder.Configuration["Auth:Audience"] ?? "froststream-api";
            // Authentik defaults the token `aud` to the OIDC client_id. The froststream blueprint
            // also ships a custom mapping that can emit `froststream-api`, so accept both: the
            // configured API audience and the client_id when one is supplied.
            var validAudiences = new[] { audience, builder.Configuration["Auth:ClientId"] }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            authBuilder
                .AddJwtBearer(options =>
                {
                    options.Authority = authority;
                    options.RequireHttpsMetadata = builder.Configuration.GetValue("Auth:RequireHttpsMetadata", true);
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateAudience = true,
                        ValidAudiences = validAudiences,
                        NameClaimType = AuthConstants.PreferredUsernameClaim,
                        RoleClaimType = AuthConstants.GroupsClaim
                    };
                });

            builder.Services.AddSingleton(sp =>
            {
                var openFgaOptions = sp.GetRequiredService<IOptions<OpenFgaOptions>>().Value;
                return new OpenFgaRuntimeState
                {
                    StoreId = NullIfBlank(openFgaOptions.StoreId),
                    AuthorizationModelId = NullIfBlank(openFgaOptions.AuthorizationModelId)
                };
            });
            builder.Services.AddHttpClient<OpenFgaAuthorizer>();
            builder.Services.AddScoped<IFrostStreamAuthorizer>(sp => sp.GetRequiredService<OpenFgaAuthorizer>());
            builder.Services.AddHttpClient<OpenFgaTupleWriter>();
            builder.Services.AddScoped<IOpenFgaTupleWriter>(sp => sp.GetRequiredService<OpenFgaTupleWriter>());
            builder.Services.AddHttpClient(OpenFgaProvisioner.HttpClientName);
            builder.Services.AddHostedService<OpenFgaProvisioner>();
            builder.Services.AddHttpClient<OpenFgaBundleManagementService>();
            builder.Services.AddScoped<IBundleManagementService>(sp => sp.GetRequiredService<OpenFgaBundleManagementService>());
            builder.Services.AddHttpClient<AuthentikDirectoryService>();
            builder.Services.AddScoped<IDirectoryService>(sp => sp.GetRequiredService<AuthentikDirectoryService>());
        }

        builder.Services.AddScoped<IAuthorizationHandler, FrostStreamPermissionHandler>();
        builder.Services.AddAuthorization(AuthPolicies.AddFrostStreamPolicies);
        // Per-endpoint policies (fs.endpoint:<id>) are resolved dynamically rather than registered up front.
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, EndpointPolicyProvider>();
        builder.Services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
        
        builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
        builder.Services.AddHttpClient<IBackupServiceClient, BackupServiceClient>(client =>
            client.BaseAddress = new Uri(builder.Configuration["BackupService:BaseUrl"] ?? "http://backupservice"));
        builder.Services.AddSingleton<BackupJobService>();
        builder.Services.AddSingleton<DownloadQueueHub>();
        builder.Services.AddHostedService<DownloadQueueHub>(p => p.GetRequiredService<DownloadQueueHub>());
        builder.Services.AddOpenBaoSecretStore(builder.Configuration);
        builder.Services.AddFrostStreamStorage();

        //var natsUrl = builder.Configuration["NATS:Url"] ?? "nats://localhost:24040";
        builder.Services.AddNats(options =>
        {
            options.Url = builder.Configuration.GetConnectionString("nats")
                          ?? builder.Configuration["NATS:Url"]
                          ?? "nats://localhost:24040";
            options.EnableTopologyProvisioning = false;
        });

        
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        // if (app.Environment.IsDevelopment())
        // {
        var exposeOpenApi = builder.Environment.IsDevelopment() ||
            builder.Configuration.GetValue("Auth:ExposeOpenApi", false);
        if (exposeOpenApi)
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference().AllowAnonymous();
        }
        //}

        if (singleUserMode)
        {
            app.Logger.LogWarning("AUTH DISABLED - SINGLE_USER_MODE is active; full access is granted to all requests.");
        }

        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex) when (context.Request.Path.StartsWithSegments("/api/global/backups"))
            {
                app.Logger.LogError(ex, "BackupService request failed.");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "The backup service is unavailable. Check its health and configured backup directory."
                });
            }
        });

        app.UseHttpsRedirection();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapDefaultEndpoints();

        app.Run();
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
