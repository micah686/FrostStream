using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Worker
{
    public static class Globals
    {
        internal static readonly string DATA_PATH = Path.Combine(Directory.GetCurrentDirectory(), "data");
        internal static readonly string TOOLS_PATH = Path.Combine(DATA_PATH, "tools");
        internal static readonly string DOWNLOAD_PATH = Path.Combine(DATA_PATH, "downloads");
        internal static readonly Guid WorkerId = Guid.NewGuid();
    }
}
