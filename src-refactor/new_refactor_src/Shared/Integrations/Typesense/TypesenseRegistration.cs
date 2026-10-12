using DataBridge.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Deployment;
using Typesense.Setup;
namespace FrostStream.Integrations.Typesense;
/// <summary>Temporary Lite dependency, isolated until Phase 5 supplies embedded search.</summary>
public static class TypesenseRegistration
{
    public static IHostApplicationBuilder AddTypesenseIntegration(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(TypesenseRegistration))) return builder;
        builder.Services.AddTypesenseClient(config =>
        {
            config.Nodes = [BuildTypesenseNode(builder.Configuration)];
            config.ApiKey = builder.Configuration["Typesense:ApiKey"] ?? "froststream-dev-key";
        });
        builder.Services.AddSingleton<TypesenseGenerationRouter>();
        builder.Services.AddSingleton<ISearchReader, TypesenseSearchReader>();
        builder.Services.AddSingleton<ISearchIndexWriter, TypesenseIndexService>();

        builder.Services.AddSingleton<TypesenseStartupService>();
        builder.Services.AddSingleton(new ApplicationInitializationStep("search collections", 50,
            (sp, ct) => sp.GetRequiredService<TypesenseStartupService>().StartAsync(ct)));
        return builder;
    }
    private static Node BuildTypesenseNode(IConfiguration configuration)
    {
        var url = configuration["Typesense:Url"] ?? configuration.GetConnectionString("typesense");
        if (!string.IsNullOrWhiteSpace(url))
        {
            var uri = new Uri(url, UriKind.Absolute);
            var port = uri.IsDefaultPort
                ? (uri.Scheme == Uri.UriSchemeHttps ? "443" : "80")
                : uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var additionalPath = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath.TrimEnd('/');
            return new Node(uri.Host, port, uri.Scheme, additionalPath);
        }

        return new Node(
            configuration["Typesense:Host"] ?? "localhost",
            configuration["Typesense:Port"] ?? "24010",
            configuration["Typesense:Protocol"] ?? "http");
    }
}
