using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal static class RecipeTransferReadGuard
{
    internal static void RequireConfiguration(long schema, ProductionStoreOptions options)
    {
        if (schema == RecipeTransferStoreOptions.SchemaVersion && options.RecipeTransfers is null)
            throw new InvalidOperationException("RecipeTransferConfigurationRequired");
        if (schema < RecipeTransferStoreOptions.SchemaVersion && options.RecipeTransfers is not null)
            throw new InvalidOperationException("RecipeTransferGovernedMigrationRequired");
        if (options.RecipeTransfers is not null &&
            (options.LocalIdentity is null || options.AuditIntegrityPolicy is null || options.RecipeDrafts is null))
            throw new InvalidOperationException("RecipeTransferRequiresIdentityAuditAndDrafts");
    }

    internal static void RequireVerified(sqlite3 database, AuditIntegrityReport report,
        StoreDeadline deadline, ProductionStoreOptions options)
    {
        RequireConfiguration(AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline), options);
        if (options.RecipeTransfers is { } transfers)
            AuditChainDatabase.RequireFullRecipeTransferVerification(database, report, deadline, transfers);
    }
}
