namespace FrostStream.DataBridge;

class Program
{
    static void Main(string[] args)
    {
        var server = new DataBridgeServer();
        server.Start();
        Console.WriteLine("DataBridge running. Press ENTER to exit.");
        Console.ReadLine();
    }
}
