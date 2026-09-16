namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private readonly object _runtimeRetirementSync = new();
    private readonly List<Func<Task>> _runtimeRetirements = new();
    private Task? _runtimeRetirement;

    internal void RegisterStorageRuntimeOwner(Func<Task> retire)
    {
        lock (_runtimeRetirementSync)
        {
            if (_runtimeRetirement is not null || Volatile.Read(ref _disposed) != 0)
                throw new InvalidOperationException("TraceStoreRetirementAlreadyStarted");
            _runtimeRetirements.Add(retire);
        }
    }

    private Task RetireStorageRuntimeOwnersAsync()
    {
        lock (_runtimeRetirementSync)
            return _runtimeRetirement ??= Task.WhenAll(_runtimeRetirements.Select(retire => Task.Run(retire)));
    }
}
