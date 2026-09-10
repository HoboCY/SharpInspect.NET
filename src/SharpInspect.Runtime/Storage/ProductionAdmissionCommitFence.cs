using System.Runtime.CompilerServices;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Only the owning writer handle is registered. Its synchronous commit
/// observer runs before SQLite commits, outside the runtime snapshot lock.</summary>
internal static class ProductionAdmissionCommitFence
{
    private static readonly ConditionalWeakTable<sqlite3, Observer> Observers = new();

    internal static void Register(sqlite3 database, Func<StoreDeadline, Action?> beforeCommit) =>
        Observers.Add(database, new Observer(beforeCommit));

    internal static Action? BeforeCommit(sqlite3 database, StoreDeadline deadline) =>
        Observers.TryGetValue(database, out var observer) ? observer.Callback(deadline) : null;

    private sealed record Observer(Func<StoreDeadline, Action?> Callback);
}
