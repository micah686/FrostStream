namespace AppHost;

/// <summary>
/// Single registry for every host port the stack binds. Two ranges, chosen to sit below
/// Linux's ephemeral port range (32768+) so a stray outgoing connection can never steal one:
/// <list type="bullet">
/// <item><b>External, 25xy0</b> — host-published in run mode and in the compose export
/// (browser/host-facing: UIs, the API, DB access). x = service, y = extra endpoints of the
/// same service. Overridable via <c>PORT_*</c> in the generated Aspire dev env.</item>
/// <item><b>Internal, 240xy</b> — container-to-container services. The compose export never
/// publishes these; they exist only so <c>aspire run</c> binds deterministic localhost ports
/// (and standalone `dotnet run` fallbacks can rely on them). x = service, y = extra endpoints.</item>
/// </list>
/// Host ports only — container target ports always stay at the image defaults.
/// Service-code fallbacks (appsettings, options defaults) must match the values here.
/// </summary>
public static class Ports
{
    // ~~~~~ External (25xy0) ~~~~~
    public static int Frontend => External("PORT_FRONTEND", 25000);
    public static int Authentik => External("PORT_AUTHENTIK", 25100);
    public static int WebApiHttp => External("PORT_WEBAPI_HTTP", 25200);
    public static int WebApiHttps => External("PORT_WEBAPI_HTTPS", 25210);
    public static int Scheduler => External("PORT_SCHEDULER", 25300);
    public static int OpenBao => External("PORT_OPENBAO", 25400);
    public static int Postgres => External("PORT_POSTGRES", 25500);
    public static int DbGate => External("PORT_DBGATE", 25600);
    public static int NatsUi => External("PORT_NATS_UI", 25700);
    public static int OpenFgaStudio => External("PORT_OPENFGA_STUDIO", 25800);

    // ~~~~~ Internal (240xy) ~~~~~
    public const int Typesense = 24010;
    public const int PotProvider = 24020;
    public const int OpenFga = 24030;
    public const int NatsClient = 24040;
    public const int NatsMonitor = 24041;
    public const int NatsWebSocket = 24042;

    private static int External(string variable, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{variable} must be a valid port number, but was '{value}'.");
        }

        return port;
    }
}
