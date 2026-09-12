using DataBridge.Data;
using Microsoft.Extensions.Logging;
using Shared.Application;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Application;

public sealed class UserNoteApplication(
    IUserNotesRepository repository,
    ILogger<UserNoteApplication> logger) : IUserNoteApplication
{
    public async Task<UserNoteResponseMessage?> UpsertAsync(
        UserNoteUpsertRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await repository.UpsertAsync(
                request.OwnerSubject,
                request.TargetType,
                request.TargetId,
                request.Note,
                cancellationToken);

            return result.Success && result.Note is not null
                ? Success(result.Note)
                : Failure(result.ErrorCode ?? "unknown", result.ErrorMessage ?? "User note operation failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed upserting user note for {OwnerSubject} {TargetType}/{TargetId}.",
                request.OwnerSubject,
                request.TargetType,
                request.TargetId);
            return Failure("internal_error", "Internal user note service error.");
        }
    }

    public async Task<UserNoteResponseMessage?> GetAsync(
        UserNoteGetRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerSubject))
                return Failure("validation", "owner is required.");

            var note = await repository.GetAsync(
                request.OwnerSubject,
                request.TargetType,
                request.TargetId,
                cancellationToken);

            return note is null ? Failure("not_found", "Note was not found.") : Success(note);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting user note for {OwnerSubject} {TargetType}/{TargetId}.",
                request.OwnerSubject,
                request.TargetType,
                request.TargetId);
            return Failure("internal_error", "Internal user note service error.");
        }
    }

    public async Task<UserNoteResponseMessage?> DeleteAsync(
        UserNoteDeleteRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerSubject))
                return Failure("validation", "owner is required.");

            var deleted = await repository.DeleteAsync(
                request.OwnerSubject,
                request.TargetType,
                request.TargetId,
                cancellationToken);

            return deleted
                ? new UserNoteResponseMessage { Success = true }
                : Failure("not_found", "Note was not found.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting user note for {OwnerSubject} {TargetType}/{TargetId}.",
                request.OwnerSubject,
                request.TargetType,
                request.TargetId);
            return Failure("internal_error", "Internal user note service error.");
        }
    }

    public async Task<UserNoteSearchResponseMessage?> SearchAsync(
        UserNoteSearchRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerSubject))
                return SearchFailure("validation", "owner is required.");

            var result = await repository.SearchAsync(
                request.OwnerSubject,
                request.Query,
                request.TargetType,
                request.PageSize,
                request.PageOffset,
                cancellationToken);

            return new UserNoteSearchResponseMessage
            {
                Success = true,
                Items = result.Items.Select(Map).ToArray(),
                TotalCount = result.TotalCount,
                HasMore = result.HasMore
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed searching user notes for {OwnerSubject}.", request.OwnerSubject);
            return SearchFailure("internal_error", "Internal user note service error.");
        }
    }

    private static UserNoteResponseMessage Success(UserNoteEntity entity)
        => new() { Success = true, Note = Map(entity) };

    private static UserNoteResponseMessage Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

    private static UserNoteSearchResponseMessage SearchFailure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

    private static UserNoteDto Map(UserNoteEntity entity)
        => new()
        {
            TargetType = entity.TargetType,
            TargetId = entity.TargetId,
            Note = entity.Note,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
}
