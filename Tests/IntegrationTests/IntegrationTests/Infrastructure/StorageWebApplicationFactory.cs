using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shared.Messaging;

namespace IntegrationTests.Infrastructure;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> that replaces <see cref="IMessageBus"/>
/// with a controllable NSubstitute mock so integration tests can run without NATS.
/// </summary>
public sealed class StorageWebApplicationFactory : WebApplicationFactory<WebAPI.Program>
{
    public IMessageBus MessageBus { get; } = Substitute.For<IMessageBus>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Remove every existing IMessageBus registration, then add the test mock.
            services.RemoveAll<IMessageBus>();
            services.AddSingleton(MessageBus);
        });
    }

    /// <summary>
    /// Pre-configures the mock to return <paramref name="response"/> for any NATS request on
    /// <paramref name="subject"/>, regardless of message type or content.
    /// </summary>
    public void SetupResponse<TRequest>(string subject, StorageOperationResponseMessage response)
    {
        MessageBus
            .RequestAsync<TRequest, StorageOperationResponseMessage>(
                subject,
                Arg.Any<TRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(response);
    }

    /// <summary>
    /// Pre-configures the mock to return <c>null</c> for any request on <paramref name="subject"/>,
    /// simulating a NATS timeout / unavailability (should yield HTTP 503).
    /// </summary>
    public void SetupNullResponse<TRequest>(string subject)
    {
        MessageBus
            .RequestAsync<TRequest, StorageOperationResponseMessage>(
                subject,
                Arg.Any<TRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns((StorageOperationResponseMessage?)null);
    }
}
