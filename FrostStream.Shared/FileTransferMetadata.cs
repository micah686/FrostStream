using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared
{
    public class FileTransferMetadata
    {
        public string FileName { get; set; }
        public ulong TotalSizeBytes { get; set; }
        public ulong Hash { get; set; }
    }

    public enum TransferMessage
    {
        MetaData,
        MetaData_EOF,
        File_EOF
    }
}
