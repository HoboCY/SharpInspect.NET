using Xunit;

// These tests create independent STA threads. Concurrent first use of WPF controls
// can deadlock ItemsControl/ScrollViewer type initialization against the shared
// BAML schema lock. Serialize test classes while retaining their real dispatchers.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
