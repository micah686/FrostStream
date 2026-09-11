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
    public static int Frontend => DeploymentRuntime.Current.Ports.Frontend;
    public static int Authentik => DeploymentRuntime.Current.Ports.Authentik;
    public static int WebApiHttp => DeploymentRuntime.Current.Ports.WebApiHttp;
    public static int WebApiHttps => DeploymentRuntime.Current.Ports.WebApiHttps;
    public static int Scheduler => DeploymentRuntime.Current.Ports.Scheduler;
    public static int OpenBao => DeploymentRuntime.Current.Ports.OpenBao;
    public static int Postgres => DeploymentRuntime.Current.Ports.Postgres;
    public static int DbGate => DeploymentRuntime.Current.Ports.DbGate;
    public static int NatsUi => DeploymentRuntime.Current.Ports.NatsUi;
    public static int OpenFgaStudio => DeploymentRuntime.Current.Ports.OpenFgaStudio;
    // Host-published so the break-glass restore wizard stays reachable when the rest of the
    // stack (frontend, Authentik) is down. The service's internal API stays on 24050.
    public static int BackupRestoreUi => DeploymentRuntime.Current.Ports.BackupRestoreUi;

    // ~~~~~ Internal (240xy) ~~~~~
    public static int Typesense => DeploymentRuntime.Current.Ports.Typesense;
    public static int PotProvider => DeploymentRuntime.Current.Ports.PotProvider;
    public static int OpenFga => DeploymentRuntime.Current.Ports.OpenFga;
    public static int NatsClient => DeploymentRuntime.Current.Ports.NatsClient;
    public static int NatsMonitor => DeploymentRuntime.Current.Ports.NatsMonitor;
    public static int NatsWebSocket => DeploymentRuntime.Current.Ports.NatsWebSocket;
    public static int BackupService => DeploymentRuntime.Current.Ports.BackupService;
    public static int ClickHouse => DeploymentRuntime.Current.Ports.ClickHouse;
}
