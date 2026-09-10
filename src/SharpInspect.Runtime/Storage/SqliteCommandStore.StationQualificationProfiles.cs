using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private static Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>?
        ReadStationQualificationProfileResolver(sqlite3 database, StoreDeadline deadline)
    {
        // Reconstruct bounded decoding dependencies from this SQLite snapshot.
        // Host configuration and signed governance authority are still checked
        // independently by the surrounding full-verification transaction.
        var configured = AuditChainDatabase.TableExists(database, "calibration_governance_store_config", deadline);
        var ledger = AuditChainDatabase.TableExists(database, "calibration_governance_events", deadline);
        AuditChainDatabase.Require(configured == ledger, "CalibrationGovernanceConfigurationMissing");
        if (!configured) return null;
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Id,FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM calibration_governance_store_config LIMIT 2;", deadline, statement => new
            {
                Id = SqliteNative.ColumnInt64(statement, 0),
                Version = SqliteNative.ColumnInt64(statement, 1),
                Options = new CalibrationGovernanceStoreOptions
                {
                    MaximumEntries = checked((int)SqliteNative.ColumnInt64(statement, 2)),
                    MaximumPayloadBytes = checked((int)SqliteNative.ColumnInt64(statement, 3)),
                    MaximumTotalBytes = SqliteNative.ColumnInt64(statement, 4)
                },
                Hash = SqliteNative.ColumnText(statement, 5)
            }).ToArray();
        AuditChainDatabase.Require(rows.Length == 1 && rows[0].Id == 1 &&
            rows[0].Version == CalibrationGovernanceStoreOptions.FormatVersion,
            "CalibrationGovernanceConfigurationMismatch");
        var row = rows[0];
        row.Options.Validate();
        AuditChainDatabase.Require(row.Hash == row.Options.BindingHash, "CalibrationGovernanceConfigurationMismatch");
        return CreateCalibrationProfileResolver(database, row.Options, deadline);
    }
}
