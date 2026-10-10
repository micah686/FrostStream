using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Shouldly;
using TUnit.Core;

namespace UnitTests.WebAPI;

public sealed class EndpointMetadataTests
{
    [Test]
    public void Every_Controller_Endpoint_Has_Detailed_OpenApi_Metadata()
    {
        var endpoints = typeof(global::WebAPI.Program).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToArray();

        endpoints.Length.ShouldBeGreaterThan(0);

        foreach (var endpoint in endpoints)
        {
            var endpointName = $"{endpoint.DeclaringType!.Name}.{endpoint.Name}";
            var summary = endpoint.GetCustomAttribute<EndpointSummaryAttribute>();
            var description = endpoint.GetCustomAttribute<EndpointDescriptionAttribute>();

            summary.ShouldNotBeNull($"{endpointName} must define EndpointSummary.");
            summary.Summary.ShouldNotBeNullOrWhiteSpace($"{endpointName} must provide a summary.");
            description.ShouldNotBeNull($"{endpointName} must define EndpointDescription.");
            description.Description.Length.ShouldBeGreaterThan(
                80,
                $"{endpointName} must provide a detailed description.");
        }
    }
}
