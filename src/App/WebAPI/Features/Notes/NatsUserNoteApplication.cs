using Conduit.NATS;
using Shared.Application;
using Shared.Messaging;

namespace WebAPI.Features.Notes;

/// <summary>Full-edition transport proxy for the shared user-note application contract.</summary>
public sealed class NatsUserNoteApplication(
    IMessageBus messageBus,
    ILogger<NatsUserNoteApplication> logger) : IUserNoteApplication
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    public Task<UserNoteResponseMessage?> UpsertAsync(
        UserNoteUpsertRequestMessage request,
        CancellationToken cancellationToken = default)
        => RequestAsync<UserNoteUpsertRequestMessage, UserNoteResponseMessage>(
            UserNoteSubjects.Upsert, request, "upsert user note", cancellationToken);

    public Task<UserNoteResponseMessage?> GetAsync(
        UserNoteGetRequestMessage request,
        CancellationToken cancellationToken = default)
        => RequestAsync<UserNoteGetRequestMessage, UserNoteResponseMessage>(
            UserNoteSubjects.Get, request, "get user note", cancellationToken);

    public Task<UserNoteResponseMessage?> DeleteAsync(
        UserNoteDeleteRequestMessage request,
        CancellationToken cancellationToken = default)
        => RequestAsync<UserNoteDeleteRequestMessage, UserNoteResponseMessage>(
            UserNoteSubjects.Delete, request, "delete user note", cancellationToken);

    public Task<UserNoteSearchResponseMessage?> SearchAsync(
        UserNoteSearchRequestMessage request,
        CancellationToken cancellationToken = default)
        => RequestAsync<UserNoteSearchRequestMessage, UserNoteSearchResponseMessage>(
            UserNoteSubjects.Search, request, "search user notes", cancellationToken);

    private async Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, TResponse>(
                subject,
                request,
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing {Operation}.", operation);
            return default;
        }
    }
}
