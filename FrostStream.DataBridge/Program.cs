using System.Threading.Tasks;

namespace FrostStream.DataBridge;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Hello, World!");
        var leaseManager = new TransferLeaseManager();
        var dr = new DataReciever(leaseManager);
        var brokerTask = Task.Run(() => leaseManager.StartBrokerLoop());
        await dr.ReceiveData();
        await brokerTask;
    }
}