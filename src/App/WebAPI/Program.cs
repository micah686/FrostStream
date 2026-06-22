using FlySwattr.NATS.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Scalar.AspNetCore;
using Shared.Auth;
using Shared.Secrets;
using Shared.Storage;
using System.Text.Json.Serialization;
using WebAPI.Auth;

namespace WebAPI;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        var singleUserMode = AuthMode.IsSingleUserMode(builder.Configuration);
        builder.Services.Configure<FrostStreamAuthOptions>(builder.Configuration.GetSection(FrostStreamAuthOptions.SectionName));
        builder.Services.Configure<OpenFgaOptions>(builder.Configuration.GetSection(OpenFgaOptions.SectionName));

        if (singleUserMode)
        {
            builder.Services
                .AddAuthentication(AuthConstants.SingleUserScheme)
                .AddScheme<AuthenticationSchemeOptions, SingleUserAuthenticationHandler>(
                    AuthConstants.SingleUserScheme,
                    _ => { });
            builder.Services.AddSingleton<IFrostStreamAuthorizer, AllowAllAuthorizer>();
        }
        else
        {
            var authority = builder.Configuration["Auth:Authority"];
            if (string.IsNullOrWhiteSpace(authority))
            {
                throw new InvalidOperationException("Auth:Authority must be configured when SINGLE_USER_MODE is not enabled.");
            }

            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = authority;
                    options.Audience = builder.Configuration["Auth:Audience"] ?? "froststream-api";
                    options.RequireHttpsMetadata = builder.Configuration.GetValue("Auth:RequireHttpsMetadata", true);
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        NameClaimType = AuthConstants.PreferredUsernameClaim,
                        RoleClaimType = AuthConstants.GroupsClaim
                    };
                });

            builder.Services.AddHttpClient<OpenFgaAuthorizer>();
            builder.Services.AddScoped<IFrostStreamAuthorizer>(sp => sp.GetRequiredService<OpenFgaAuthorizer>());
        }

        builder.Services.AddScoped<IAuthorizationHandler, FrostStreamPermissionHandler>();
        builder.Services.AddAuthorization(options =>
        {
            var authenticatedSystemAccess = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new FrostStreamPermissionRequirement(AuthConstants.AccessRelation, AuthConstants.SystemObject))
                .Build();

            options.FallbackPolicy = authenticatedSystemAccess;
            options.AddPolicy("SystemAccess", authenticatedSystemAccess);
        });
        builder.Services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
        
        builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
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
}
