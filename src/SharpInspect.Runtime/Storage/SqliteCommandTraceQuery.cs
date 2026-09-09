using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;

namespace SharpInspect.Runtime.Storage;

/// <summary>Bounded read-only command-trace capability. Every call owns and closes its read connection.</summary>
public sealed class SqliteCommandTraceQuery : ICommandTraceQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim QuerySlots = new(4, 4);
    private static int _outstandingQueries;

    public SqliteCommandTraceQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<CommandTracePage> QueryAsync(CommandTraceFilter filter,
        CancellationToken cancellationToken = default)
    {
        ValidateFilter(filter);
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.QueryTimeout < TimeSpan.FromMilliseconds(1) || _options.QueryTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(_options.QueryTimeout));
        var deadline = new StoreDeadline(_options.QueryTimeout);
        if (Interlocked.Increment(ref _outstandingQueries) > 64)
        {
            Interlocked.Decrement(ref _outstandingQueries);
            throw new InvalidOperationException("TraceQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("TraceQueryDeadlineExceeded");
            entered = await QuerySlots.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("TraceQueryDeadlineExceeded");
            return await Task.Run(() =>
                {
                    SqliteNative.EnsureDeadline(deadline, cancellationToken);
                    // Filesystem and registry inspection must not block the WPF Dispatcher.
                    if (!StoragePathValidator.TryValidate(_options, out var currentPath, out var pathReason))
                        throw new InvalidOperationException(pathReason);
                    return QueryCore(currentPath, filter, deadline, cancellationToken, _options);
                }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (SqliteNativeException ex)
        {
            throw new InvalidOperationException(ex.ReasonCode, ex);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException("TraceStoreUnavailable", ex);
        }
        finally
        {
            if (entered) QuerySlots.Release();
            Interlocked.Decrement(ref _outstandingQueries);
        }
    }

    private static CommandTracePage QueryCore(string databasePath, CommandTraceFilter filter, StoreDeadline deadline,
        CancellationToken cancellationToken, ProductionStoreOptions options)
    {
        using var connection = SqliteNative.Open(databasePath, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; PRAGMA foreign_keys=ON;", deadline, cancellationToken);

        var schemaVersion = SqliteNative.WithStatement(database, "PRAGMA user_version;", deadline,
            statement =>
            {
                SqliteNative.Step(database, statement, deadline, cancellationToken);
                return checked((int)SqliteNative.ColumnInt64(statement, 0));
             }, cancellationToken);
        SqliteNative.ConfigureSqliteLimit(database, options, schemaVersion);
        if (schemaVersion is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18)) throw new InvalidOperationException("StoreSchemaUnavailable");
        if (options.PlcResultContracts is not null && schemaVersion < PlcResultContractStoreOptions.SchemaVersion)
            throw new InvalidOperationException("PlcResultContractGovernedMigrationRequired");
        if (options.PlcResultContracts is null &&
            (schemaVersion == PlcResultContractStoreOptions.SchemaVersion ||
                schemaVersion == RecipeActivationStoreOptions.SchemaVersion))
            throw new InvalidOperationException("PlcResultContractConfigurationRequired");
        if ((schemaVersion == PlcResultContractStoreOptions.SchemaVersion ||
                schemaVersion == RecipeActivationStoreOptions.SchemaVersion) &&
            (options.RecipeDrafts is null || options.RecipeReleases is null ||
                options.LocalIdentity is null || options.AuditIntegrityPolicy is null))
            throw new InvalidOperationException("PlcResultContractsRequiresDraftsReleasesIdentityAndAudit");
        if (options.RecipeReleases is not null && schemaVersion < RecipeReleaseStoreOptions.SchemaVersion)
            throw new InvalidOperationException("RecipeReleaseGovernedMigrationRequired");
        if (options.RecipeReleases is null &&
            (schemaVersion == RecipeReleaseStoreOptions.SchemaVersion ||
                schemaVersion == PlcResultContractStoreOptions.SchemaVersion ||
                schemaVersion == RecipeActivationStoreOptions.SchemaVersion))
            throw new InvalidOperationException("RecipeReleaseConfigurationRequired");
        if ((schemaVersion == RecipeReleaseStoreOptions.SchemaVersion ||
                schemaVersion == PlcResultContractStoreOptions.SchemaVersion ||
                schemaVersion == RecipeActivationStoreOptions.SchemaVersion) && options.RecipeDrafts is null)
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if (schemaVersion == RecipeActivationStoreOptions.SchemaVersion && options.RecipeActivations is null)
            throw new InvalidOperationException("RecipeActivationConfigurationRequired");
        if (options.CalibrationSessions is not null && schemaVersion < CalibrationSessionStoreOptions.SchemaVersion)
            throw new InvalidOperationException("CalibrationGovernedMigrationRequired");
        if (options.CalibrationSessions is null &&
            (schemaVersion is CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion))
            throw new InvalidOperationException("CalibrationConfigurationRequired");
        if (options.CalibrationGovernance is not null && schemaVersion < CalibrationGovernanceStoreOptions.SchemaVersion)
            throw new InvalidOperationException("CalibrationGovernanceMigrationRequired");
        if (options.CalibrationGovernance is null && schemaVersion == CalibrationGovernanceStoreOptions.SchemaVersion)
            throw new InvalidOperationException("CalibrationGovernanceConfigurationRequired");
        if ((schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeActivationStoreOptions.SchemaVersion) && options.CameraSetup is null)
            throw new InvalidOperationException("CameraSetupConfigurationRequired");
        if (schemaVersion < CameraSetupStoreOptions.SchemaVersion && options.CameraSetup is not null)
            throw new InvalidOperationException("CameraSetupGovernedMigrationRequired");
        if ((schemaVersion is CameraRecoveryStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion) &&
            options.CameraRecovery is null)
            throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
        if (schemaVersion < CameraRecoveryStoreOptions.SchemaVersion && options.CameraRecovery is not null)
            throw new InvalidOperationException("CameraRecoveryGovernedMigrationRequired");
        if (schemaVersion == CameraNetworkStoreOptions.SchemaVersion && options.CameraNetwork is null)
            throw new InvalidOperationException("CameraNetworkConfigurationRequired");
        if (schemaVersion < CameraNetworkStoreOptions.SchemaVersion && options.CameraNetwork is not null)
            throw new InvalidOperationException("CameraNetworkGovernedMigrationRequired");
        if ((schemaVersion is ImagingSetupStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion) &&
            options.ImagingSetup is null)
            throw new InvalidOperationException("ImagingSetupConfigurationRequired");
        if (schemaVersion < ImagingSetupStoreOptions.SchemaVersion && options.ImagingSetup is not null)
            throw new InvalidOperationException("ImagingSetupGovernedMigrationRequired");
        if (schemaVersion == AlgorithmResultArchiveOptions.SchemaVersion && options.AlgorithmResultArchive is null)
            throw new InvalidOperationException("AlgorithmResultArchiveConfigurationRequired");
        if (schemaVersion < AlgorithmResultArchiveOptions.SchemaVersion && options.AlgorithmResultArchive is not null)
            throw new InvalidOperationException("AlgorithmResultArchiveGovernedMigrationRequired");
        if (schemaVersion == RecipeDraftStoreOptions.SchemaVersion && options.RecipeDrafts is null)
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if (schemaVersion < RecipeDraftStoreOptions.SchemaVersion && options.RecipeDrafts is not null)
            throw new InvalidOperationException("RecipeDraftGovernedMigrationRequired");
        if (schemaVersion == ImagingSetupStoreOptions.SchemaVersion)
            RequireImagingSchemaConfiguration(database, options, deadline, cancellationToken);
        if (schemaVersion is CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion)
            RequireCalibrationSchemaConfiguration(database, options, deadline, cancellationToken);
        if (schemaVersion == RecipeReleaseStoreOptions.SchemaVersion)
        {
            Integrity.AuditChainDatabase.RequireReleaseLedgerPresence(database, deadline,
                options.AlgorithmResultArchive is not null, options.CameraSetup is not null,
                options.CameraRecovery is not null, options.CameraNetwork is not null,
                options.ImagingSetup is not null, options.CalibrationSessions is not null,
                options.CalibrationGovernance is not null, release: true);
            SqliteCommandStore.RequireConfiguredRecipeReleases(database, options.RecipeReleases!, deadline);
            SqliteCommandStore.RequireConfiguredRecipeDrafts(database, options.RecipeDrafts!, deadline);
            if (options.AlgorithmResultArchive is not null)
                SqliteCommandStore.RequireConfiguredArchive(database, options.AlgorithmResultArchive, deadline);
            if (options.CameraSetup is not null)
                SqliteCommandStore.RequireConfiguredCameraSetup(database, options.CameraSetup, deadline);
            if (options.CameraRecovery is not null)
                SqliteCommandStore.RequireConfiguredCameraRecovery(database, options.CameraRecovery, deadline);
            if (options.CameraNetwork is not null)
                SqliteCommandStore.RequireConfiguredCameraNetwork(database, options.CameraNetwork, deadline);
            if (options.ImagingSetup is not null)
                SqliteCommandStore.RequireConfiguredImagingSetup(database, options.ImagingSetup, deadline);
            if (options.CalibrationSessions is not null)
                SqliteCommandStore.RequireConfiguredCalibrationSessions(database, options.CalibrationSessions, deadline);
            if (options.CalibrationGovernance is not null)
                SqliteCommandStore.RequireConfiguredCalibrationGovernance(database, options.CalibrationGovernance, deadline);
        }
        if (schemaVersion == RecipeActivationStoreOptions.SchemaVersion)
        {
            Integrity.AuditChainDatabase.RequireReleaseLedgerPresence(database, deadline,
                options.AlgorithmResultArchive is not null, options.CameraSetup is not null,
                options.CameraRecovery is not null, options.CameraNetwork is not null,
                options.ImagingSetup is not null, options.CalibrationSessions is not null,
                options.CalibrationGovernance is not null, release: true,
                plcResultContract: true, activation: true);
            SqliteCommandStore.RequireConfiguredRecipeDrafts(database, options.RecipeDrafts!, deadline);
            SqliteCommandStore.RequireConfiguredRecipeReleases(database, options.RecipeReleases!, deadline);
            SqliteCommandStore.RequireConfiguredPlcResultContracts(database,
                options.PlcResultContracts!, deadline);
            SqliteCommandStore.RequireConfiguredRecipeActivations(database,
                options.RecipeActivations!, deadline);
            if (options.AlgorithmResultArchive is not null)
                SqliteCommandStore.RequireConfiguredArchive(database, options.AlgorithmResultArchive, deadline);
            if (options.CameraSetup is not null)
                SqliteCommandStore.RequireConfiguredCameraSetup(database, options.CameraSetup, deadline);
            if (options.CameraRecovery is not null)
                SqliteCommandStore.RequireConfiguredCameraRecovery(database, options.CameraRecovery, deadline);
            if (options.CameraNetwork is not null)
                SqliteCommandStore.RequireConfiguredCameraNetwork(database, options.CameraNetwork, deadline);
            if (options.ImagingSetup is not null)
                SqliteCommandStore.RequireConfiguredImagingSetup(database, options.ImagingSetup, deadline);
            if (options.CalibrationSessions is not null)
                SqliteCommandStore.RequireConfiguredCalibrationSessions(database, options.CalibrationSessions, deadline);
            if (options.CalibrationGovernance is not null)
                SqliteCommandStore.RequireConfiguredCalibrationGovernance(database, options.CalibrationGovernance, deadline);
        }
        if (schemaVersion == PlcResultContractStoreOptions.SchemaVersion)
        {
            Integrity.AuditChainDatabase.RequireReleaseLedgerPresence(database, deadline,
                options.AlgorithmResultArchive is not null, options.CameraSetup is not null,
                options.CameraRecovery is not null, options.CameraNetwork is not null,
                options.ImagingSetup is not null, options.CalibrationSessions is not null,
                options.CalibrationGovernance is not null, release: true,
                plcResultContract: true);
            SqliteCommandStore.RequireConfiguredPlcResultContracts(database,
                options.PlcResultContracts!, deadline);
            if (options.RecipeReleases is not null)
            {
                SqliteCommandStore.RequireConfiguredRecipeReleases(database, options.RecipeReleases, deadline);
                SqliteCommandStore.RequireConfiguredRecipeDrafts(database, options.RecipeDrafts!, deadline);
            }
            if (options.AlgorithmResultArchive is not null)
                SqliteCommandStore.RequireConfiguredArchive(database, options.AlgorithmResultArchive, deadline);
            if (options.CameraSetup is not null)
                SqliteCommandStore.RequireConfiguredCameraSetup(database, options.CameraSetup, deadline);
            if (options.CameraRecovery is not null)
                SqliteCommandStore.RequireConfiguredCameraRecovery(database, options.CameraRecovery, deadline);
            if (options.CameraNetwork is not null)
                SqliteCommandStore.RequireConfiguredCameraNetwork(database, options.CameraNetwork, deadline);
            if (options.ImagingSetup is not null)
                SqliteCommandStore.RequireConfiguredImagingSetup(database, options.ImagingSetup, deadline);
            if (options.CalibrationSessions is not null)
                SqliteCommandStore.RequireConfiguredCalibrationSessions(database, options.CalibrationSessions, deadline);
            if (options.CalibrationGovernance is not null)
                SqliteCommandStore.RequireConfiguredCalibrationGovernance(database, options.CalibrationGovernance, deadline);
        }
        var latestPosition = SqliteNative.WithStatement(database, "SELECT COALESCE(MAX(Position),0) FROM command_facts;",
            deadline, statement =>
        {
            SqliteNative.Step(database, statement, deadline, cancellationToken);
            return SqliteNative.ColumnInt64(statement, 0);
        }, cancellationToken);
        if (filter.ThroughPosition is { } requested && requested > latestPosition)
            throw new InvalidOperationException("TraceCursorInvalid");
        var through = filter.ThroughPosition is { } requestedPosition
            ? requestedPosition : latestPosition;
        if (through < filter.AfterPosition || through == 0)
            return new CommandTracePage(new ReadOnlyCollection<CommandTraceRecord>(Array.Empty<CommandTraceRecord>()),
                through, null);

        var predicates = new List<string> { "Position > ?", "Position <= ?" };
        if (filter.CorrelationId is not null) predicates.Add("CorrelationId = ?");
        if (filter.ClaimedPrincipalId is not null) predicates.Add("ClaimedPrincipalId = ?");
        var where = string.Join(" AND ", predicates);
        var sql = $"SELECT Position, EventId, AttemptId, CorrelationId, RuntimeEpoch, EventVersion, " +
            "AggregateSequence, OccurredAtUtc, SystemPrincipalId, AuthenticatedHumanPrincipalId, " +
            "CommandKind, Source, ClaimedPrincipalId, ClaimedSessionId, ClaimedStepUpGrantId, " +
            $"Phase, Disposition, ReasonCode FROM command_facts WHERE {where} " +
            "ORDER BY Position LIMIT ?;";

        var rows = SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            var parameter = 1;
            SqliteNative.BindInt64(database, statement, parameter++, filter.AfterPosition);
            SqliteNative.BindInt64(database, statement, parameter++, through);
            if (filter.CorrelationId is { } correlation)
                SqliteNative.BindGuid(database, statement, parameter++, correlation);
            if (filter.ClaimedPrincipalId is { } principal)
                SqliteNative.BindText(database, statement, parameter++, principal);
            SqliteNative.BindInt(database, statement, parameter, checked(filter.PageSize + 1));

            var result = new List<CommandTraceRecord>(filter.PageSize + 1);
            while (SqliteNative.Step(database, statement, deadline, cancellationToken) == SQLitePCL.raw.SQLITE_ROW)
                result.Add(ReadRecord(statement));
            return result;
        }, cancellationToken);

        long? next = null;
        if (rows.Count > filter.PageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            next = rows[^1].Position;
        }

        return new CommandTracePage(new ReadOnlyCollection<CommandTraceRecord>(rows), through, next);
    }

    private static void RequireImagingSchemaConfiguration(SQLitePCL.sqlite3 database,
        ProductionStoreOptions options, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        // Schema 13 makes Recovery and Network independent optional ledgers.
        // Its version alone cannot establish their presence or configuration.
        void RequireOptionalLedger(string configTable, string eventTable, bool configured, string reason)
        {
            var count = SqliteNative.WithStatement(database,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN (?,?);",
                deadline, statement =>
                {
                    SqliteNative.BindText(database, statement, 1, configTable);
                    SqliteNative.BindText(database, statement, 2, eventTable);
                    SqliteNative.Step(database, statement, deadline, cancellationToken);
                    return SqliteNative.ColumnInt64(statement, 0);
                }, cancellationToken);
            if (count != (configured ? 2 : 0)) throw new InvalidOperationException(reason);
        }

        RequireOptionalLedger("camera_recovery_store_config", "camera_recovery_terminal_events",
            options.CameraRecovery is not null, "CameraRecoveryConfigurationRequired");
        RequireOptionalLedger("camera_network_store_config", "camera_network_events",
            options.CameraNetwork is not null, "CameraNetworkConfigurationRequired");
        RequireOptionalLedger("algorithm_result_archive_config", "development_algorithm_results",
            options.AlgorithmResultArchive is not null, "AlgorithmResultArchiveConfigurationRequired");
        RequireOptionalLedger("recipe_draft_store_config", "recipe_draft_revisions",
            options.RecipeDrafts is not null, "RecipeDraftConfigurationRequired");
        SqliteCommandStore.RequireConfiguredCameraSetup(database, options.CameraSetup!, deadline);
        SqliteCommandStore.RequireConfiguredImagingSetup(database, options.ImagingSetup!, deadline);
        if (options.CameraRecovery is not null)
            SqliteCommandStore.RequireConfiguredCameraRecovery(database, options.CameraRecovery, deadline);
        if (options.CameraNetwork is not null)
            SqliteCommandStore.RequireConfiguredCameraNetwork(database, options.CameraNetwork, deadline);
        if (options.AlgorithmResultArchive is not null)
            SqliteCommandStore.RequireConfiguredArchive(database, options.AlgorithmResultArchive, deadline);
        if (options.RecipeDrafts is not null)
            SqliteCommandStore.RequireConfiguredRecipeDrafts(database, options.RecipeDrafts, deadline);
    }

    private static void RequireCalibrationSchemaConfiguration(SQLitePCL.sqlite3 database,
        ProductionStoreOptions options, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var count = SqliteNative.WithStatement(database,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN (?,?);",
            deadline, statement =>
            {
                SqliteNative.BindText(database, statement, 1, "camera_recovery_store_config");
                SqliteNative.BindText(database, statement, 2, "camera_recovery_terminal_events");
                SqliteNative.Step(database, statement, deadline, cancellationToken);
                return SqliteNative.ColumnInt64(statement, 0);
            }, cancellationToken);
        if (count != 2 || options.CameraRecovery is null)
            throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
        SqliteCommandStore.RequireConfiguredCameraSetup(database, options.CameraSetup!, deadline);
        SqliteCommandStore.RequireConfiguredCameraRecovery(database, options.CameraRecovery, deadline);
        SqliteCommandStore.RequireConfiguredImagingSetup(database, options.ImagingSetup!, deadline);
        SqliteCommandStore.RequireConfiguredCalibrationSessions(database, options.CalibrationSessions!, deadline);
        if (options.CalibrationGovernance is not null)
            SqliteCommandStore.RequireConfiguredCalibrationGovernance(database, options.CalibrationGovernance, deadline);
    }

    private static CommandTraceRecord ReadRecord(SQLitePCL.sqlite3_stmt statement)
    {
        var sourceText = SqliteNative.ColumnText(statement, 11);
        var dispositionText = SqliteNative.ColumnText(statement, 16);
        return new CommandTraceRecord(
            SqliteNative.ColumnInt64(statement, 0),
            ParseGuid(SqliteNative.ColumnText(statement, 1)),
            ParseGuid(SqliteNative.ColumnText(statement, 2)),
            ParseGuid(SqliteNative.ColumnText(statement, 3)),
            ParseGuid(SqliteNative.ColumnText(statement, 4)),
            checked((int)SqliteNative.ColumnInt64(statement, 5)),
            checked((int)SqliteNative.ColumnInt64(statement, 6)),
            ParseTime(SqliteNative.ColumnText(statement, 7)),
            SqliteNative.ColumnText(statement, 8) ?? throw new InvalidOperationException("TraceStoreCorrupt"),
            SqliteNative.ColumnText(statement, 9),
            (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 10),
            ParseNullableEnum<CommandSource>(sourceText),
            SqliteNative.ColumnText(statement, 12),
            ParseNullableGuid(SqliteNative.ColumnText(statement, 13)),
            ParseNullableGuid(SqliteNative.ColumnText(statement, 14)),
            (CommandAuditPhase)SqliteNative.ColumnInt64(statement, 15),
            ParseNullableEnum<CommandDisposition>(dispositionText),
            SqliteNative.ColumnText(statement, 17) ?? throw new InvalidOperationException("TraceStoreCorrupt"));
    }

    private static void ValidateFilter(CommandTraceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.AfterPosition < 0) throw new ArgumentOutOfRangeException(nameof(filter.AfterPosition));
        if (filter.ThroughPosition is < 0) throw new ArgumentOutOfRangeException(nameof(filter.ThroughPosition));
        if (filter.PageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(filter.PageSize));
        if (filter.ClaimedPrincipalId?.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(filter.ClaimedPrincipalId));
    }

    private static Guid ParseGuid(string? value) => Guid.TryParse(value, out var parsed)
        ? parsed : throw new InvalidOperationException("TraceStoreCorrupt");

    private static Guid? ParseNullableGuid(string? value) => string.IsNullOrEmpty(value) ? null : ParseGuid(value);

    private static T? ParseNullableEnum<T>(string? value) where T : struct, Enum =>
        string.IsNullOrEmpty(value) ? null :
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
        Enum.IsDefined(typeof(T), number) ? (T)Enum.ToObject(typeof(T), number) : throw new InvalidOperationException("TraceStoreCorrupt");

    private static DateTimeOffset ParseTime(string? value) => DateTimeOffset.TryParse(value,
        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
        ? parsed : throw new InvalidOperationException("TraceStoreCorrupt");
}
