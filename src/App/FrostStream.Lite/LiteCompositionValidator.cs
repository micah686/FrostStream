using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace FrostStream.Lite;

public static class LiteCompositionValidator
{
    public static void ValidateServices(IServiceCollection services)
    {
        var duplicateHostedServices = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .GroupBy(ImplementationIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (duplicateHostedServices.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate hosted-service registrations: {string.Join(", ", duplicateHostedServices)}.");
        }
    }

    public static void ValidateRoutes(WebApplication app)
    {
        var collisions = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                Pattern = endpoint.RoutePattern.RawText ?? string.Empty,
                Methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]
            })
            .SelectMany(endpoint => endpoint.Methods.Select(method => $"{method} {endpoint.Pattern}"))
            .GroupBy(route => route, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(route => route, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (collisions.Length > 0)
        {
            throw new InvalidOperationException(
                $"Lite route collisions detected: {string.Join(", ", collisions)}.");
        }
    }

    private static string ImplementationIdentity(ServiceDescriptor descriptor)
        => descriptor.ImplementationType?.FullName
           ?? descriptor.ImplementationInstance?.GetType().FullName
           ?? descriptor.ImplementationFactory?.Method.ToString()
           ?? "unknown";
}
