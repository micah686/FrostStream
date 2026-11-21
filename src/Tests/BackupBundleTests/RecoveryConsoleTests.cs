using BackupServiceNew.Recovery;
using Microsoft.Extensions.Logging;

internal static class RecoveryConsoleTests
{
    public static void Run()
    {
        var configured = new RecoveryCredential("primary", "alias");
        Require(configured.Matches("primary") && !configured.Matches("alias"), "Primary token precedence");
        Require(new RecoveryCredential("  ", "alias").Matches("alias"), "Blank primary retains alias");
        var logger = new CaptureLogger();
        configured.LogGeneratedToken(logger);
        Require(logger.Messages.Count == 0, "Configured tokens must not be logged");
        var generated = new RecoveryCredential(null, " ");
        generated.LogGeneratedToken(logger);
        generated.LogGeneratedToken(logger);
        Require(logger.Messages.Count == 1, "Generated token must be logged once");
        var token = logger.Messages[0].Split('\n')[2];
        Require(token.Length == 64 && Convert.FromHexString(token).Length == 32, "256-bit token");
        Require(generated.Matches(token) && !generated.Matches("") && !generated.Matches(token + "x"), "Generated credential matching");
        var next = new RecoveryCredential("", null);
        Require(!next.Matches(token), "Fresh token per service instance");
        Console.WriteLine("Passed: token precedence, alias, random generation, matching, and one-time logging.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
