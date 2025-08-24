namespace FrostStream.Worker;

class Program
{

    static void Main(string[] args)
    {

        var worker = new Worker(Globals.WorkerId.ToString());
        Console.Title = $"ContentCitadel Agent - {Globals.WorkerId}";

        Console.WriteLine($"Starting ContentCitadel Agent with ID: {Globals.WorkerId}");

        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("CTRL+C pressed - shutting down agent...");
            e.Cancel = true;
        };

        worker.Run();
    }
}