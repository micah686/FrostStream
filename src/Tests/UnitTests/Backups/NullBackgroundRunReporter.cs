using Shared.Messaging;

namespace UnitTests.Backups;

/// <summary>
/// No-op <see cref="IBackgroundRunReporter"/> for tests. Lived in Shared until 2026-08-05, but no
/// production host ever resolved it — every service that runs scheduled work wires the real
/// message-bus reporter.
/// </summary>
public sealed class NullBackgroundRunReporter : IBackgroundRunReporter
{
    public static readonly NullBackgroundRunReporter Instance = new();

    public Task<IBackgroundRunScope> BeginAsync(
        string taskType, ScheduledBackgroundRequest request, string? detail = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IBackgroundRunScope>(new Scope());

    public Task<IBackgroundRunScope> BeginScheduledAsync(
        string taskType, string scheduleKey, string idempotencyKey, string? detail = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IBackgroundRunScope>(new Scope());

    public Task<IBackgroundRunScope> BeginManualAsync(
        string taskType, string idempotencyKey, string? detail = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IBackgroundRunScope>(new Scope());

    private sealed class Scope : IBackgroundRunScope
    {
        public Guid RunId { get; } = Guid.Empty;
        public Task ReportAsync(string message, int? current = null, int? total = null, double? percent = null) => Task.CompletedTask;
        public void Succeed(string? summary = null) { }
        public void Fail(string errorMessage) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
