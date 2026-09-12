using DataBridge.Application;
using DataBridge.AudioRenditions;
using DataBridge.Data;
using DataBridge.MediaStream;
using DataBridge.Metadata;
using DataBridge.Renditions;
using DataBridge.Statistics;
using DataBridge.StreamRenditions;
using Microsoft.Extensions.DependencyInjection;
using Shared.Application;

namespace DataBridge;

/// <summary>
/// Reusable synchronous application layer. Composition roots may register this module without
/// invoking DataBridge's executable entry point or enabling any NATS consumers.
/// </summary>
public static class DataBridgeApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddDataBridgeApplicationOperations(this IServiceCollection services)
    {
        services.AddDurableWorkflowAbstractions();
        services.AddScoped<IDownloadJobsRepository, DownloadJobsRepository>();
        services.AddScoped<IDownloadFlowV2Repository, DownloadFlowV2Repository>();
        services.AddScoped<IImportSessionRepository, ImportSessionRepository>();
        services.AddScoped<IMetadataRepository, MetadataRepository>();
        services.AddScoped<IMetadataReadService, MetadataReadService>();
        services.AddScoped<IStatisticsReadService, StatisticsReadService>();
        services.AddScoped<IMediaStreamReadService, MediaStreamReadService>();
        services.AddScoped<IMediaThumbnailReadService, MediaThumbnailReadService>();
        services.AddScoped<IMediaThumbnailGenerationService, MediaThumbnailGenerationService>();
        services.AddScoped<IMediaCaptionReadService, MediaCaptionReadService>();
        services.AddScoped<IAccountAssetReadService, AccountAssetReadService>();
        services.AddScoped<IAudioRenditionRepository, AudioRenditionRepository>();
        services.AddScoped<IMediaEncodingStatusRepository, MediaEncodingStatusRepository>();
        services.AddScoped<IStreamRenditionRepository, StreamRenditionRepository>();
        services.AddScoped<IRenditionQueueRepository, RenditionQueueRepository>();
        services.AddScoped<IPlaylistsRepository, PlaylistsRepository>();
        services.AddScoped<IUserPlaylistsRepository, UserPlaylistsRepository>();
        services.AddScoped<IUserNotesRepository, UserNotesRepository>();
        services.AddScoped<IOptionPresetsRepository, OptionPresetsRepository>();
        services.AddScoped<IDownloadConfigSetsRepository, DownloadConfigSetsRepository>();
        services.AddScoped<IScheduledTasksRepository, ScheduledTasksRepository>();
        services.AddScoped<ICreatorDiscoveryRepository, CreatorDiscoveryRepository>();
        services.AddScoped<IUserNoteApplication, UserNoteApplication>();
        services.AddScoped<IDownloadWorkflowIngress, DownloadWorkflowIngress>();
        services.AddSingleton<IDownloadWorkflowStarter, CleipnirDownloadWorkflowStarter>();
        return services;
    }
}
