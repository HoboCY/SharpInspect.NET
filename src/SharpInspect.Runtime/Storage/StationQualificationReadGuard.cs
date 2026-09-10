using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal static class StationQualificationReadGuard
{
    internal static void RequireConfiguration(long schema, ProductionStoreOptions options)
    {
        if (schema == StationQualificationStoreOptions.SchemaVersion && options.StationQualifications is null)
            throw new InvalidOperationException("StationQualificationConfigurationRequired");
        if (schema < StationQualificationStoreOptions.SchemaVersion && options.StationQualifications is not null)
            throw new InvalidOperationException("StationQualificationGovernedMigrationRequired");
        if (schema == StationQualificationStoreOptions.SchemaVersion &&
            (options.LocalIdentity is null || options.AuditIntegrityPolicy is null))
            throw new InvalidOperationException("StationQualificationRequiresIdentityAndAudit");
    }

    internal static void RequireVerified(sqlite3 database, AuditIntegrityReport report,
        StoreDeadline deadline, ProductionStoreOptions options)
    {
        if (options.StationQualifications is { } qualification)
            AuditChainDatabase.RequireFullStationQualificationVerification(database, report, deadline, qualification);
    }
}
