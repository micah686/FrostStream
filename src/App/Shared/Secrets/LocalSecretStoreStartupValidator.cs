using Microsoft.Extensions.Hosting;

namespace Shared.Secrets;

public sealed class LocalSecretStoreStartupValidator(LocalFileSecretStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => store.ValidateExistingSecretsAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
