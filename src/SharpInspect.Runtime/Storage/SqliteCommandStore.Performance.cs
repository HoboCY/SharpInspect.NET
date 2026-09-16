namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private long _performanceCheckpointAttempts;
    // The queue reservation remains held through physical completion, so this includes the active writer.
    internal int? PerformanceReservedWrites => _queueSlots is null ? null : _options.QueueCapacity - _queueSlots.CurrentCount;
    internal long PerformanceCheckpointAttempts => Interlocked.Read(ref _performanceCheckpointAttempts);
}
