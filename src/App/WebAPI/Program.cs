using FlySwattr.NATS.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Scalar.AspNetCore;
using Shared.Auth;
using Shared.Secrets;
using Shared.Storage;
using System.Text.Json.Serialization;
using WebAPI.Auth;
using WebAPI.Features.Backups;

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
        builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));

        if (singleUserMode)
        {
            builder.Services
                .AddAuthentication(AuthConstants.SingleUserScheme)
                .AddScheme<AuthenticationSchemeOptions, SingleUserAuthenticationHandler>(
                    AuthConstants.SingleUserScheme,
                    _ => { });
            builder.Services.AddSingleton<IFrostStreamAuthorizer, AllowAllAuthorizer>();
            builder.Services.AddSingleton<IOpenFgaTupleWriter, NullOpenFgaTupleWriter>();
            builder.Services.AddSingleton<IBundleManagementService, NullBundleManagementService>();
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

            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
        builder.Services.AddSingleton<BackupJobService>();
        builder.Services.AddOpenBaoSecretStore(builder.Configuration);
        builder.Services.AddFrostStreamStorage();

        //var natsUrl = builder.Configuration["NATS:Url"] ?? "nats://localhost:4222";
        builder.Services.AddEnterpriseNATSMessaging(options =>
        {
            options.Core.Url = builder.Configuration.GetConnectionString("nats")
                               ?? builder.Configuration["NATS:Url"]
                               ?? "nats://localhost:4222";
            //options.Core.Url = natsUrl;
            options.EnableTopologyProvisioning = false;
            options.EnablePayloadOffloading = false;
            options.EnableResilience = false;
            options.EnableCaching = false;
            options.EnableDistributedLock = false;
            options.EnableDlqAdvisoryListener = false;
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

        app.UseHttpsRedirection();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapDefaultEndpoints();

        app.Run();
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
