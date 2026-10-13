using MediaProcessor.Audio;
using MediaProcessor.Ffmpeg;
using MediaProcessor.Storage;
using MediaProcessor.Thumbnails;
using MediaProcessor.Video;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Shared.Messaging;

using Shared.Deployment;

namespace MediaProcessor;

public static class MediaProcessorModule
{
    public static IHostApplicationBuilder AddMediaProcessorModule(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(MediaProcessorModule))) return builder;
        builder.AddDeployment();


        builder.Services.AddOptions<MediaProcessorOptions>()
            .Bind(builder.Configuration.GetSection(MediaProcessorOptions.SectionName))
            .Validate(options => options.MaxConcurrentProcesses is >= 1 and <= 16, "MediaProcessor:MaxConcurrentProcesses must be between 1 and 16.")
            .ValidateOnStart();
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddKeyedSingleton<IBackgroundRunReporter>("media-processor", (sp, _) => new BackgroundRunReporter(
            sp.GetRequiredService<FrostStream.ApplicationContracts.IMessageBus>(),
            sp.GetRequiredService<IClock>(),
            "media-processor",
            sp.GetService<Microsoft.Extensions.Logging.ILogger<BackgroundRunReporter>>()));
        builder.Services.AddSingleton<FfmpegRunner>();
        builder.Services.AddHostedService<AudioRenditionProcessorService>();
        builder.Services.AddHostedService<StreamRenditionProcessorService>();
        builder.Services.AddHostedService<MediaThumbnailGenerationService>();

        return builder;
    }


}
