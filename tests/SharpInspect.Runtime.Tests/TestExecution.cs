using Xunit;

// Each acceptance fixture owns real SQLite writers, audit verification and timed
// device/session work. Bound independent fixture concurrency so machines with many
// logical processors do not turn scheduling pressure into unrelated deadline failures.
// Explicit concurrency and watchdog behavior inside each test remain unchanged.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
