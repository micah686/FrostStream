using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared
{
    /// <summary>
    /// Metadata describing a pending file transfer from a worker.
    /// This is sent before the binary payload so the DataBridge can
    /// validate size, allocate storage and associate the connection
    /// with a previously issued lease.
    /// </summary>
    public class FileTransferMetadata
    {
        public Guid JobId { get; set; }
        public Guid LeaseId { get; set; }
        public string WorkerId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public ulong TotalSizeBytes { get; set; }
        public ulong Hash { get; set; }

        /// <summary>
        /// Optional additional metadata as a JSON string
        /// (e.g. subtitles, codecs, etc.).
        /// </summary>
        public string? ExtraMetadataJson { get; set; }
    }

    public enum TransferMessage
    {
        MetaData,
        MetaData_EOF,
        File_EOF
    }
}
