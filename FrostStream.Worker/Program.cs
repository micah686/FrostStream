namespace FrostStream.Worker;

class Program
{
    
    internal static readonly string DATA_PATH = Path.Combine(Directory.GetCurrentDirectory(), "data");
    internal static readonly string TOOLS_PATH = Path.Combine(DATA_PATH, "tools");
    internal static readonly string DOWNLOAD_PATH = Path.Combine(DATA_PATH, "downloads");
    static void Main(string[] args)
    {
        var agentId = args.Length > 0 ? args[0] : Guid.NewGuid().ToString();
        var worker = new Worker(agentId);
        Console.Title = $"ContentCitadel Agent - {agentId}";

        Console.WriteLine($"Starting ContentCitadel Agent with ID: {agentId}");

        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("CTRL+C pressed - shutting down agent...");
            e.Cancel = true;
        };

        worker.Run();
    }
}