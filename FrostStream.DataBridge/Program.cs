namespace FrostStream.DataBridge;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Hello, World!");
        var dr = new DataReciever();
        await dr.ReceiveData();
        Console.ReadLine();

    }
}