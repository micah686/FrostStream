using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using DataBridge.LiveChat;
using DataBridge.Search;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using DataBridge.Persistence;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Orchestrates user-initiated video deletion. A delete removes the physical storage objects
/// (via the Worker) before touching the database, so a partial failure leaves the records
/// intact and can be retried safely (the Worker delete is idempotent).
///
/// Two modes are supported:
/// <list type="bullet">
/// <item>Global: remove every storage copy, the metadata record, and search-index entries.</item>
/// <item>Per storage key: remove only the copy on one storage key. When that key holds the last
/// remaining copy, the operation cascades to a full delete.</item>
/// </list>
/// </summary>
public sealed class MediaDeleteExecutor
{
    private static readonly TimeSpan WorkerRequestTimeout = TimeSpan.FromMinutes(5);

    private readonly ApplicationDatabase _dataSource;
    private readonly IMessageBus _messageBus;
    private readonly ITypesenseIndexService _searchIndex;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MediaDeleteExecutor> _logger;

    public MediaDeleteExecutor(
        ApplicationDatabase dataSource,
        IMessageBus messageBus,
        ITypesenseIndexService searchIndex,
        IServiceScopeFactory scopeFactory,
        ILogger<MediaDeleteExecutor> logger)
    {
        _dataSource = dataSource;
        _messageBus = messageBus;
        _searchIndex = searchIndex;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<MediaDeleteResponse> DeleteMediaAsync(Guid mediaGuid, CancellationToken cancellationToken = default)
    {
        if (!await MediaExistsAsync(mediaGuid, cancellationToken))
        {
            return Failure("not_found", $"Media '{mediaGuid}' was not found.");
        }

        if (await HasActiveDownloadJobAsync(mediaGuid, cancellationToken))
        {
            return Failure("conflict", $"Media '{mediaGuid}' has an active download job and cannot be deleted.");
        }

        return await DeleteEntireMediaAsync(mediaGuid, cancellationToken);
    }

    public async Task<MediaDeleteResponse> DeleteMediaForStorageKeyAsync(
        Guid mediaGuid,
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return Failure("validation", "Storage key is required.");
        }

        if (!await MediaExistsAsync(mediaGuid, cancellationToken))
        {
            return Failure("not_found", $"Media '{mediaGuid}' was not found.");
        }

        var (onKey, total) = await CountContentVersionsAsync(mediaGuid, storageKey, cancellationToken);
        if (onKey == 0)
        {
            return Failure("not_found", $"Media '{mediaGuid}' has no content stored on storage key '{storageKey}'.");
        }

        if (await HasActiveDownloadJobAsync(mediaGuid, cancellationToken))
        {
            return Failure("conflict", $"Media '{mediaGuid}' has an active download job and cannot be deleted.");
        }

        // Removing the last remaining copy is equivalent to deleting the whole video.
        if (onKey == total)
        {
            return await DeleteEntireMediaAsync(mediaGuid, cancellationToken);
        }

        var files = await LoadMediaFilesForKeyAsync(mediaGuid, storageKey, cancellationToken);
        var deletion = await DeletePhysicalFilesAsync(files, cancellationToken);
        if (!deletion.Success)
        {
            return Failure(deletion.ErrorCode!, deletion.ErrorMessage!, deletion.Deleted);
        }

        if (!await DeleteStorageKeyRowsAsync(mediaGuid, storageKey, files, cancellationToken))
            return Failure("conflict", "Media changed while its files were being deleted. Retry after active downloads finish.", deletion.Deleted);

        _logger.LogInformation(
            "Deleted storage-key copy of media {MediaGuid} on '{StorageKey}' ({FilesDeleted} files).",
            mediaGuid,
            storageKey,
            deletion.Deleted);

        return new MediaDeleteResponse { Success = true, FilesDeleted = deletion.Deleted, MediaRemoved = false };
    }

    private async Task<MediaDeleteResponse> DeleteEntireMediaAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var files = await LoadAllMediaFilesAsync(mediaGuid, cancellationToken);
        var deletion = await DeletePhysicalFilesAsync(files, cancellationToken);
        if (!deletion.Success)
        {
            return Failure(deletion.ErrorCode!, deletion.ErrorMessage!, deletion.Deleted);
        }

        if (!await DeleteMediaRowAsync(mediaGuid, files, cancellationToken))
            return Failure("conflict", "Media changed while its files were being deleted. Retry after active downloads finish.", deletion.Deleted);
        await DeleteFromSearchIndexAsync(mediaGuid, cancellationToken);
        await DeleteLiveChatAsync(mediaGuid, cancellationToken);

        _logger.LogInformation(
            "Deleted media {MediaGuid} entirely ({FilesDeleted} files).",
            mediaGuid,
            deletion.Deleted);

        return new MediaDeleteResponse { Success = true, FilesDeleted = deletion.Deleted, MediaRemoved = true };
    }

    private async Task<FileDeletionResult> DeletePhysicalFilesAsync(
        IReadOnlyList<MediaFileLocation> files,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var file in files)
        {
            DeleteMediaFileResponse? response;
            try
            {
                response = await _messageBus.RequestAsync<DeleteMediaFileRequest, DeleteMediaFileResponse>(
                    MediaFileSubjects.Delete,
                    new DeleteMediaFileRequest { StorageKey = file.StorageKey, StoragePath = file.StoragePath },
                    WorkerRequestTimeout,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Worker request failed deleting media file {StorageKey}:{StoragePath}.",
                    file.StorageKey,
                    file.StoragePath);
                return FileDeletionResult.Failed("unavailable", ex.Message, deleted);
            }

            if (response is not { Success: true })
            {
                return FileDeletionResult.Failed(
                    response?.ErrorCode ?? "unavailable",
                    response?.ErrorMessage ?? "Worker did not respond.",
                    deleted);
            }

            deleted++;
        }

        return FileDeletionResult.Succeeded(deleted);
    }

    private async Task<bool> MediaExistsAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            _dataSource.Sql("MediaDeleteExecutor.MediaExistsAsync.1"));
        command.Parameters.AddWithValue("id", mediaGuid);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task<bool> HasActiveDownloadJobAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_dataSource.Sql("MediaDeleteExecutor.HasActiveDownloadJobAsync.1"));
        command.Parameters.AddWithValue("id", mediaGuid);
        DownloadJobStateSql.AddActiveStatesParameter(command);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task<(int OnKey, int Total)> CountContentVersionsAsync(
        Guid mediaGuid,
        string storageKey,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_dataSource.Sql("MediaDeleteExecutor.CountContentVersionsAsync.1"));
        command.Parameters.AddWithValue("id", mediaGuid);
        command.Parameters.AddWithValue("key", storageKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task<IReadOnlyList<MediaFileLocation>> LoadAllMediaFilesAsync(
        Guid mediaGuid,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_dataSource.Sql("MediaDeleteExecutor.LoadAllMediaFilesAsync.1"));
        command.Parameters.AddWithValue("id", mediaGuid);

        var files = new List<MediaFileLocation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new MediaFileLocation(reader.GetString(0), reader.GetString(1)));
        }

        return files;
    }

    private async Task<IReadOnlyList<MediaFileLocation>> LoadMediaFilesForKeyAsync(
        Guid mediaGuid,
        string storageKey,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_dataSource.Sql("MediaDeleteExecutor.LoadMediaFilesForKeyAsync.1"));
        command.Parameters.AddWithValue("id", mediaGuid);
        command.Parameters.AddWithValue("key", storageKey);

        var files = new List<MediaFileLocation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new MediaFileLocation(storageKey, reader.GetString(0)));
        }

        return files;
    }

    private Task<bool> DeleteMediaRowAsync(Guid mediaGuid, IReadOnlyList<MediaFileLocation> files, CancellationToken ct)
        => DeleteRowsAsync(mediaGuid, null, files, ct);

    private Task<bool> DeleteStorageKeyRowsAsync(Guid mediaGuid, string storageKey, IReadOnlyList<MediaFileLocation> files, CancellationToken ct)
        => DeleteRowsAsync(mediaGuid, storageKey, files, ct);

    private async Task<bool> DeleteRowsAsync(Guid mediaGuid, string? storageKey, IReadOnlyList<MediaFileLocation> expectedFiles, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        return await db.MutateAsync("Media.DeleteRows", async () =>
        {
            var connection = db.Database.GetDbConnection();
            var transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            await using (var active = ApplicationDbCommands.Create(connection, _dataSource.Sql("MediaDeleteExecutor.HasActiveDownloadJobAsync.1"), transaction))
            {
                active.Parameters.AddWithValue("id", mediaGuid);
                DownloadJobStateSql.AddActiveStatesParameter(active);
                if (Convert.ToBoolean(await active.ExecuteScalarAsync(ct))) return false;
            }
            // Filesystem/network deletion happens once, before this retryable callback. Refuse to
            // remove rows added by a writer after that snapshot, including new storage objects.
            await using (var snapshot = ApplicationDbCommands.Create(connection, _dataSource.Sql(storageKey is null
                ? "MediaDeleteExecutor.LoadAllMediaFilesAsync.1" : "MediaDeleteExecutor.LoadMediaFilesForKeyAsync.1"), transaction))
            {
                snapshot.Parameters.AddWithValue("id", mediaGuid);
                if (storageKey is not null) snapshot.Parameters.AddWithValue("key", storageKey);
                var current = new HashSet<MediaFileLocation>();
                await using var reader = await snapshot.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) current.Add(storageKey is null
                    ? new(reader.GetString(0), reader.GetString(1)) : new(storageKey, reader.GetString(0)));
                if (!current.SetEquals(expectedFiles)) return false;
            }
            await using var command = ApplicationDbCommands.Create(connection, _dataSource.Sql(storageKey is null
                ? "MediaDeleteExecutor.DeleteMediaRowAsync.1" : "MediaDeleteExecutor.DeleteStorageKeyRowsAsync.1"), transaction);
            command.Parameters.AddWithValue("id", mediaGuid);
            if (storageKey is not null) command.Parameters.AddWithValue("key", storageKey);
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);
    }

    private async Task DeleteFromSearchIndexAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var id = mediaGuid.ToString();
        await _searchIndex.DeleteMediaByGuidAsync(id, cancellationToken);
        await _searchIndex.DeleteCommentsByMediaGuidAsync(id, cancellationToken);
        await _searchIndex.DeleteCaptionsByMediaGuidAsync(id, cancellationToken);
    }

    /// <summary>
    /// Best-effort removal of the media's chat replay (ClickHouse rows + application database marker).
    /// Resolves the ingest service lazily — it is only registered when live chat is enabled —
    /// and never fails the delete: orphaned chat rows are invisible without their media.
    /// </summary>
    private async Task DeleteLiveChatAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<LiveChatIngestService>() is { } liveChat)
            {
                await liveChat.DeleteForMediaAsync(mediaGuid, cancellationToken);
                return;
            }

            // Feature disabled: still clear the application database marker so HasLiveChat stays truthful.
            await using var command = _dataSource.CreateCommand(
                _dataSource.Sql("MediaDeleteExecutor.DeleteLiveChatAsync.1"));
            command.Parameters.AddWithValue("id", mediaGuid);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not remove the live chat replay for deleted media {MediaGuid}; continuing.",
                mediaGuid);
        }
    }

    private static MediaDeleteResponse Failure(string errorCode, string errorMessage, int filesDeleted = 0)
        => new()
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            FilesDeleted = filesDeleted
        };

    private readonly record struct MediaFileLocation(string StorageKey, string StoragePath);

    private sealed record FileDeletionResult(bool Success, int Deleted, string? ErrorCode, string? ErrorMessage)
    {
        public static FileDeletionResult Succeeded(int deleted) => new(true, deleted, null, null);

        public static FileDeletionResult Failed(string errorCode, string errorMessage, int deleted)
            => new(false, deleted, errorCode, errorMessage);
    }
}
