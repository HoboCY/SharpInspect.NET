using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Read-side boundary for the schema-25 trace-storage-policy ledger.  The
/// policy ledger is independent from the optional recipe-transfer ledger, but
/// a schema-25 database must still have identity and audit authority before a
/// read can expose any governed projection.
/// </summary>
internal static class TraceStoragePolicyReadGuard
{
    internal static void RequireConfiguration(long schema, ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (schema is TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion &&
            options.TraceStoragePolicies is null)
            throw new InvalidOperationException("TraceStoragePolicyConfigurationRequired");
        if (schema < TraceStoragePolicyStoreOptions.SchemaVersion &&
            options.TraceStoragePolicies is not null)
            throw new InvalidOperationException("TraceStoragePolicyGovernedMigrationRequired");
        if (options.TraceStoragePolicies is not null &&
            (options.LocalIdentity is null || options.AuditIntegrityPolicy is null))
            throw new InvalidOperationException("TraceStoragePolicyRequiresIdentityAndAudit");
    }

    internal static void RequireVerified(sqlite3 database, AuditIntegrityReport report,
        StoreDeadline deadline, ProductionStoreOptions options)
    {
        var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
        RequireConfiguration(schema, options);
        if (options.TraceStoragePolicies is { } traceStoragePolicies)
            AuditChainDatabase.RequireFullTraceStoragePolicyVerification(database, report,
                deadline, traceStoragePolicies);
    }
}
