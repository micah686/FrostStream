namespace FrostStream.MessageHub;

class Program
{
    static void Main(string[] args)
    {
        Console.Title = "MessageBroker";

        using var broker = new Broker();

        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("Shutting down broker...");
            e.Cancel = true;
            broker.Stop();
            _shutdown.Set();
        };

        var brokerThread = new Thread(broker.Start) { IsBackground = true };
        brokerThread.Start();

        Console.WriteLine("Press Ctrl+C to stop the broker...");
        _shutdown.WaitOne();

        Console.WriteLine("Broker stopped.");
    }

    private static readonly ManualResetEvent _shutdown = new(false);
}
