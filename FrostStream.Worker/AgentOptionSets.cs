using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using YoutubeDLSharp.Options;

namespace FrostStream.Worker;

internal class AgentOptionSets
{
    internal static OptionSet DefaultOptionSet => new OptionSet
    {
        WriteInfoJson = true,
        RestrictFilenames = true,
        EmbedThumbnail = true,
        EmbedChapters = true,
        EmbedMetadata = true,
        NoWritePlaylistMetafiles = true,
        NoPart = true,
    };

    internal static OptionSet LiveChatOptionSet => new OptionSet
    {
        WriteSubs = true,
        SubLangs = "live_chat",
        SkipDownload = true,
        IgnoreNoFormatsError = true,
        WriteInfoJson = true,
        RestrictFilenames = true,
        EmbedThumbnail = true,
        EmbedChapters = true,
        EmbedMetadata = true,
        NoWritePlaylistMetafiles = true,
        NoPart = true,
    };
}
