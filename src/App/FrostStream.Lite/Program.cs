using DataBridge.Lite;
using FrostStream.Lite;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Application;
using Shared.Secrets;
using System.Text.Json.Serialization;

var command = LiteCommandLine.Parse(args);
if (command != LiteCommand.Run)
{
    throw new NotSupportedException(
        $"FrostStream Lite command '{command.ToString().ToLowerInvariant()}' is reserved but is not implemented in Phase 8.");
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
LiteProductionValidation.Validate(builder.Configuration, builder.Environment);

builder.Services.AddSingleton<ICurrentOwner, FixedCurrentOwner>();
builder.Services.AddLocalFileSecretStore(builder.Configuration);
builder.Services.AddDataBridgeLiteApiOperations(builder.Configuration);
builder.Services.AddLiteDurableExecution(builder.Configuration);
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddHealthChecks().AddCheck<LiteDatabaseReadinessCheck>("lite-database");

LiteCompositionValidator.ValidateServices(builder.Services);
var app = builder.Build();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapHealthChecks("/alive", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});
LiteCompositionValidator.ValidateRoutes(app);

await app.RunAsync();

namespace FrostStream.Lite
{
    public partial class Program;
}
