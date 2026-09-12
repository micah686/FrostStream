using Shared.Messaging;

namespace Shared.Application;

/// <summary>
/// Transport-independent user-note operations. Requests retain an explicit owner so adapters and
/// direct callers share identical ownership, validation, and error semantics.
/// </summary>
public interface IUserNoteApplication
{
    Task<UserNoteResponseMessage?> UpsertAsync(
        UserNoteUpsertRequestMessage request,
        CancellationToken cancellationToken = default);

    Task<UserNoteResponseMessage?> GetAsync(
        UserNoteGetRequestMessage request,
        CancellationToken cancellationToken = default);

    Task<UserNoteResponseMessage?> DeleteAsync(
        UserNoteDeleteRequestMessage request,
        CancellationToken cancellationToken = default);

    Task<UserNoteSearchResponseMessage?> SearchAsync(
        UserNoteSearchRequestMessage request,
        CancellationToken cancellationToken = default);
}
