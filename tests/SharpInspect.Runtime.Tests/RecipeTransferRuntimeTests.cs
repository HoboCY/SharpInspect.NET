using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Runtime acceptance for the schema-24 transfer boundary.  The package and
/// content codecs have their own tests; these cases exercise the real identity,
/// Step-Up, SQLite draft writer, signing-key, trust and read-only query paths.
/// </summary>
public sealed class RecipeTransferRuntimeTests
{
    [Fact]
    public async Task V138_R01_ExportAndImportCreatesNewDraftOnlyWithIndependentColdReads()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);

        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        Assert.NotNull(exported.Export);

        var imported = await harness.ImportAsync(exported.Export!.ToArray());
        Assert.True(imported.Succeeded, imported.Outcome.ReasonCode);
        Assert.NotNull(imported.Draft);
        Assert.NotNull(imported.Import);
        Assert.NotEqual(harness.SourceRevision.DraftId, imported.Draft!.DraftId);
        Assert.Equal(1, imported.Draft.Revision);
        Assert.False(imported.Draft.Published);
        Assert.False(imported.Draft.Active);
        Assert.Null(imported.Draft.Content.MigrationLineage);
        Assert.StartsWith("import.", imported.Draft.Content.RecipeKey, StringComparison.Ordinal);

        await harness.Fixture.WaitForVerifiedAsync();
        var query = new SqliteRecipeTransferQuery(harness.Fixture.Options);
        var trust = await query.ReadTrustAsync();
        Assert.True(trust.Available, trust.ReasonCode);
        Assert.Equal(key.Signer.KeyId, Assert.Single(trust.Trust!.Signers).KeyId);
        var keys = await query.ReadSigningKeysAsync();
        Assert.True(keys.Available, keys.ReasonCode);
        Assert.Contains(keys.Keys, item => item.KeyId == key.Signer.KeyId && !item.Retired);
        var provenance = await query.ReadImportAsync(imported.Draft.DraftId);
        Assert.True(provenance.Available, provenance.ReasonCode);
        Assert.Equal(imported.Import.PackageBytesHash, provenance.Provenance!.PackageBytesHash);
        var history = await query.QueryAsync(new RecipeTransferFilter(PageSize: 32));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Contains(history.Records, item => item.Kind == RecipeTransferEventKind.Imported);
        Assert.Contains(history.Records, item => item.Kind == RecipeTransferEventKind.Exported);
    }

    [Fact]
    public async Task V138_R02_ExactImportOperationReplayDoesNotCreateAnotherDraft()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);

        var operationId = Guid.NewGuid();
        var firstCommand = await harness.BuildImportCommandAsync(
            exported.Export!.ToArray(), issueStepUp: true, operationId: operationId);
        var first = await harness.Service.ImportAsync(firstCommand.Command,
            exported.Export.ToArray());
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        var before = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;");

        // The writer recognizes the exact operation from durable history.  A
        // replay reuses the original invocation/grant and must return the first draft.
        var replayCommand = await harness.BuildImportCommandAsync(exported.Export.ToArray(),
            issueStepUp: false, operationId: operationId,
            existingStepUpGrantId: firstCommand.StepUpGrantId);
        var replay = await harness.Service.ImportAsync(replayCommand.Command,
            exported.Export.ToArray());
        Assert.True(replay.Succeeded, replay.Outcome.ReasonCode);
        Assert.Equal(first.Outcome, replay.Outcome);
        Assert.Equal(first.Draft!.DraftId, replay.Draft!.DraftId);
        Assert.Equal(first.Import!.PackageBytesHash, replay.Import!.PackageBytesHash);
        Assert.Equal(before, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V138_R03_AnonymousImportIsRejectedWithoutDraftMutation()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        var before = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;");

        var loggedOut = await harness.Fixture.Sessions.LogoutAsync(
            harness.Fixture.Sessions.Current.SessionId);
        Assert.True(loggedOut.Succeeded, loggedOut.ReasonCode);
        var bytes = exported.Export!.ToArray();
        var command = new ImportRecipeTransferCommand(Guid.NewGuid(), new CommandInvocation(CommandSource.Integration),
            Convert.ToHexString(SHA256.HashData(bytes)), "anonymous import");
        var rejected = await harness.Service.ImportAsync(command, bytes);

        Assert.False(rejected.Succeeded);
        Assert.Contains(rejected.Outcome.ReasonCode,
            new[] { "AuthenticationRequired", "SessionMismatch", "PermissionDenied" });
        Assert.Equal(before, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V138_R04_TrustAndKeyManagementRequireFreshStepUp()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var keyCommand = new CreateRecipeSigningKeyCommand(Guid.NewGuid(), harness.Fixture.Invocation(),
            "no-stepup-key", "recipe-data", now.AddMinutes(-1), now.AddHours(1), "missing step-up");
        var keyRejected = await harness.Service.CreateSigningKeyAsync(keyCommand);
        Assert.False(keyRejected.Succeeded);
        Assert.Equal("StepUpRequired", keyRejected.Outcome.ReasonCode);

        var trustCommand = new ReplaceRecipeTrustStoreCommand(Guid.NewGuid(), harness.Fixture.Invocation(),
            0, Array.Empty<RecipeTrustedSigner>(), "missing step-up trust");
        var trustRejected = await harness.Service.ReplaceTrustAsync(trustCommand);
        Assert.False(trustRejected.Succeeded);
        Assert.Equal("StepUpRequired", trustRejected.Outcome.ReasonCode);

        await harness.Fixture.WaitForVerifiedAsync();
        var query = new SqliteRecipeTransferQuery(harness.Fixture.Options);
        Assert.Empty((await query.ReadSigningKeysAsync()).Keys);
        Assert.Null((await query.ReadTrustAsync()).Trust);
    }

    [Fact]
    public async Task V138_R05_RemovingCurrentTrustRejectsTheOldPackageWithoutHalfDraft()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        await harness.ReplaceTrustAsync();
        var before = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;");

        var rejected = await harness.ImportAsync(exported.Export!.ToArray(), issueStepUp: false);
        Assert.False(rejected.Succeeded);
        Assert.Equal("RecipeTransferSignerUntrusted", rejected.Outcome.ReasonCode);
        Assert.Equal(before, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V138_R06_RetiredSigningKeyCannotExportAgain()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var first = await harness.ExportAsync();
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        var retired = await harness.RetireSigningKeyAsync(key);
        Assert.True(retired.Succeeded, retired.Outcome.ReasonCode);

        var second = await harness.ExportAsync(issueStepUp: false);
        Assert.False(second.Succeeded);
        Assert.Equal("RecipeTransferSigningKeyUnavailable", second.Outcome.ReasonCode);
    }

    [Fact]
    public async Task V138_R07_TamperedOrTrailingPackageIsRejectedWithoutDraftMutation()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        var before = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;");

        var tampered = exported.Export!.ToArray();
        tampered[tampered.Length / 2] ^= 0x01;
        var changed = await harness.ImportAsync(tampered, issueStepUp: false);
        Assert.False(changed.Succeeded);
        Assert.Contains("RecipeTransfer", changed.Outcome.ReasonCode, StringComparison.Ordinal);

        var trailing = exported.Export.ToArray().Concat(new byte[] { 0x00 }).ToArray();
        var extra = await harness.ImportAsync(trailing, issueStepUp: false);
        Assert.False(extra.Succeeded);
        Assert.Contains("RecipeTransfer", extra.Outcome.ReasonCode, StringComparison.Ordinal);
        Assert.Equal(before, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V138_R08_PreCancelledImportDoesNotReachTheWriter()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        var bytes = exported.Export!.ToArray();
        var command = new ImportRecipeTransferCommand(Guid.NewGuid(), harness.Fixture.Invocation(),
            Convert.ToHexString(SHA256.HashData(bytes)), "pre-cancelled import");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            harness.Service.ImportAsync(command, bytes, cancellation.Token).AsTask());
        Assert.Equal(1, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        var query = new SqliteRecipeTransferQuery(harness.Fixture.Options);
        Assert.False((await query.ReadImportAsync(Guid.NewGuid())).Available);
    }

    [Fact]
    public async Task V138_R09_CancelledInFlightImportRollsBackDraftAndProvenance()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        var bytes = exported.Export!.ToArray();
        var before = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;");
        var command = await harness.BuildImportCommandAsync(bytes);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = harness.Fixture.Options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }

        using var cancellation = new CancellationTokenSource();
        var pending = harness.Service.ImportAsync(command.Command, bytes, cancellation.Token).AsTask();
        await Task.Yield();
        await Task.Delay(25);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await using (var commit = connection.CreateCommand())
        {
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync();
        }

        RecipeTransferResult? result = null;
        try
        {
            result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation is also a valid terminal surface; either
            // surface must leave the transaction without a draft or provenance.
        }
        if (result is not null) Assert.False(result.Succeeded);
        Assert.Equal(before, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        Assert.Equal(0, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_transfer_import_provenance;"));
    }

    [Fact]
    public async Task V138_R10_TrustedSignedUnknownSchemaIsRejectedWithoutHalfDraft()
    {
        await using var harness = await TransferHarness.CreateAsync();
        using var externalKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var trusted = new RecipeTrustedSigner("external-schema-key",
            Convert.ToBase64String(externalKey.ExportSubjectPublicKeyInfo()), "recipe-data",
            now.AddMinutes(-1), now.AddHours(1));
        await harness.ReplaceTrustAsync(trusted);

        Assert.True(RecipeTransferContentCodec.TryEncode(harness.SourceRevision.Content,
            harness.PortablePolicy, out var validRecipe, out var projection, out var reason), reason);
        var root = JsonNode.Parse(validRecipe!)!.AsObject();
        root["Algorithm"]!["ConfigurationSchema"]!["ContentHash"] = new string('A', 64);
        var unknownSchemaRecipe = Encoding.UTF8.GetBytes(root.ToJsonString());
        var manifest = new RecipeTransferPackageManifest(Guid.NewGuid(),
            new RecipeTransferSource(Guid.NewGuid(), "external.recipe", 1,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("source"))),
                RecipeTransferSourceLifecycle.Draft, "external-station"), now,
            new RecipeTransferSignatureInfo(trusted.KeyId, trusted.Scope),
            RecipeTransferContentCodec.Dependencies(projection!),
            new[] { new RecipeTransferMemberManifest(RecipeTransferPackageLimits.RecipeMemberPath,
                unknownSchemaRecipe.Length, Convert.ToHexString(SHA256.HashData(unknownSchemaRecipe))) },
            RecipeTransferPackageManifest.ZeroContentHash);
        manifest = RecipeTransferPackageCodec.FinalizeManifest(manifest, unknownSchemaRecipe);
        var signature = externalKey.SignData(RecipeTransferPackageCodec.GetSignatureInput(manifest),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var package = RecipeTransferPackageCodec.Encode(manifest, unknownSchemaRecipe, signature);
        var before = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;");

        var command = await harness.BuildImportCommandAsync(package);
        var rejected = await harness.Service.ImportAsync(command.Command, package);
        Assert.False(rejected.Succeeded);
        Assert.Equal("RecipeTransferSchemaIncompatible", rejected.Outcome.ReasonCode);
        Assert.Equal(before, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        Assert.Equal(0, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_transfer_import_provenance;"));
    }

    [Fact]
    public async Task V138_R11_ReleasedSnapshotExportsItsExactLineageAndImportsAsDraftOnly()
    {
        await using var harness = await TransferHarness.CreateAsync(includeRelease: true);
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var released = await harness.ReleaseSourceAsync();
        Assert.True(harness.Factory!.ValidationCalls > 0);
        var validationsBeforeTransfer = harness.Factory!.ValidationCalls;

        var exported = await harness.ExportSelectionAsync(new RecipeTransferSourceSelection(
            released.Reference, released.Record.ReleaseId, released.Record.ContentHash));
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        Assert.NotNull(exported.Export);
        Assert.True(RecipeTransferPackageCodec.TryRead(exported.Export!.ToArray(), out var package,
            out var packageReason), packageReason);
        Assert.Equal(RecipeTransferSourceLifecycle.Released, package!.Manifest.Source.Lifecycle);
        Assert.Equal(released.Record.ReleaseId, package.Manifest.Source.SourceId);
        Assert.Equal(released.Reference.Id, package.Manifest.Source.RecipeKey);
        Assert.Equal(released.Record.RecipeVersion, package.Manifest.Source.Revision);
        Assert.Equal(released.Record.ContentHash, package.Manifest.Source.RevisionContentHash);

        var imported = await harness.ImportAsync(exported.Export.ToArray());
        Assert.True(imported.Succeeded, imported.Outcome.ReasonCode);
        Assert.NotNull(imported.Draft);
        Assert.NotNull(imported.Import);
        Assert.Equal(RecipeTransferSourceLifecycle.Released.ToString(), imported.Import!.SourceLifecycle);
        Assert.Equal(released.Record.ReleaseId.ToString("D") + ":" + released.Reference.Id,
            imported.Import.SourceRecipeIdentity);
        Assert.Equal(released.Record.RecipeVersion.ToString(), imported.Import.SourceRevision);
        Assert.Equal(released.Record.ContentHash, imported.Import.SourceSnapshotHash);
        Assert.NotEqual(released.Record.Source.DraftId, imported.Draft!.DraftId);
        Assert.Equal(1, imported.Draft.Revision);
        Assert.False(imported.Draft.Published);
        Assert.False(imported.Draft.Active);
        Assert.Null(imported.Draft.Content.MigrationLineage);
        Assert.Equal(validationsBeforeTransfer, harness.Factory.ValidationCalls);

        var transferEventsBeforeMismatch = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_transfer_events WHERE Kind = 4;");
        var provenanceBeforeMismatch = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_transfer_import_provenance;");
        var mismatch = await harness.ExportSelectionAsync(new RecipeTransferSourceSelection(
            released.Reference, released.Record.ReleaseId, new string('A', 64)));
        Assert.False(mismatch.Succeeded);
        Assert.Equal("RecipeTransferExportSourceUnavailable", mismatch.Outcome.ReasonCode);
        Assert.Equal(transferEventsBeforeMismatch, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_transfer_events WHERE Kind = 4;"));
        Assert.Equal(provenanceBeforeMismatch, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_transfer_import_provenance;"));
    }

    [Fact]
    public async Task V138_R12_TamperedImportProvenanceIsRejectedByTransferAndDraftBoundaries()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        var imported = await harness.ImportAsync(exported.Export!.ToArray());
        Assert.True(imported.Succeeded, imported.Outcome.ReasonCode);
        var draftId = imported.Draft!.DraftId;
        await harness.Fixture.WaitForVerifiedAsync();

        var draftCount = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;");
        var provenanceCount = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_transfer_import_provenance;");
        var transferEventCount = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_transfer_events;");
        await ExecuteSqlAsync(harness.Fixture.Options.DatabasePath,
            "DROP TRIGGER recipe_transfer_import_immutable_update;");
        try
        {
            await ExecuteSqlAsync(harness.Fixture.Options.DatabasePath, @"
                UPDATE recipe_transfer_import_provenance
                SET SourceLifecycle='Released' WHERE DraftId=$draftId;",
                ("$draftId", draftId.ToString("D")));
        }
        finally
        {
            await ExecuteSqlAsync(harness.Fixture.Options.DatabasePath, @"
                CREATE TRIGGER recipe_transfer_import_immutable_update
                BEFORE UPDATE ON recipe_transfer_import_provenance BEGIN
                    SELECT RAISE(ABORT,'ImmutableRecipeTransferImport');
                END;");
        }

        var transferRead = await harness.Service.ReadImportAsync(draftId);
        Assert.False(transferRead.Available);
        var coldTransferRead = await new SqliteRecipeTransferQuery(harness.Fixture.Options)
            .ReadImportAsync(draftId);
        Assert.False(coldTransferRead.Available);
        var draftRead = await new SqliteRecipeDraftQuery(harness.Fixture.Options)
            .ReadAsync(draftId, 1);
        Assert.False(draftRead.Available);

        var save = await harness.Fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            harness.Fixture.Document("tampered provenance must block writes"),
            "tampered provenance write boundary");
        Assert.False(save.Saved);
        Assert.Equal(draftCount, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_draft_revisions;"));
        Assert.Equal(provenanceCount, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_transfer_import_provenance;"));
        Assert.Equal(transferEventCount, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM recipe_transfer_events;"));
    }

    [Fact]
    public async Task V138_R13_TransferLedgerExhaustionRollsBackTheEntireImport()
    {
        await using var harness = await TransferHarness.CreateAsync(maximumTransfers: 1);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var trusted = new RecipeTrustedSigner("capacity-source", Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()),
            "recipe-data", now.AddMinutes(-1), now.AddHours(1));
        await harness.ReplaceTrustAsync(trusted);
        Assert.True(RecipeTransferContentCodec.TryEncode(harness.SourceRevision.Content, harness.PortablePolicy,
            out var recipe, out var projected, out var reason), reason);
        var manifest = new RecipeTransferPackageManifest(Guid.NewGuid(),
            new(Guid.NewGuid(), "external.recipe", 1, harness.SourceRevision.RevisionContentHash,
                RecipeTransferSourceLifecycle.Draft), now, new(trusted.KeyId, trusted.Scope),
            RecipeTransferContentCodec.Dependencies(projected!),
            new[] { new RecipeTransferMemberManifest(RecipeTransferPackageLimits.RecipeMemberPath,
                recipe!.Length, Convert.ToHexString(SHA256.HashData(recipe))) }, RecipeTransferPackageManifest.ZeroContentHash);
        manifest = RecipeTransferPackageCodec.FinalizeManifest(manifest, recipe!);
        var bytes = RecipeTransferPackageCodec.Encode(manifest, recipe!, signer.SignData(
            RecipeTransferPackageCodec.GetSignatureInput(manifest), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var command = await harness.BuildImportCommandAsync(bytes);
        await harness.Fixture.WaitForVerifiedAsync();
        var beforeAudit = harness.Fixture.Scalar("SELECT COUNT(*) FROM audit_entries;");
        var rejected = await harness.Service.ImportAsync(command.Command, bytes);
        Assert.False(rejected.Succeeded);
        Assert.Equal("RecipeTransferEntryCapacityExceeded", rejected.Outcome.ReasonCode);
        Assert.Null(rejected.Draft);
        Assert.Null(rejected.Import);
        Assert.Equal(1, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        Assert.Equal(0, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_import_provenance;"));
        Assert.Equal(1, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events;"));
        Assert.Equal(beforeAudit, harness.Fixture.Scalar("SELECT COUNT(*) FROM audit_entries;"));
    }

    [Fact]
    public async Task V138_R14_ReusedCorrelationCannotChangeTheAuthorizedImportTarget()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var export = await harness.ExportAsync();
        Assert.True(export.Succeeded, export.Outcome.ReasonCode);
        var bytes = export.Export!.ToArray();
        var original = await harness.BuildImportCommandAsync(bytes);
        var first = await harness.Service.ImportAsync(original.Command, bytes);
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        var drafts = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;");
        var events = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events;");
        var changed = new ImportRecipeTransferCommand(original.Command.CorrelationId, original.Command.Invocation,
            original.Command.PackageBytesHash, "different authorized reason");
        Assert.NotEqual(original.Command.AuthorizationTarget, changed.AuthorizationTarget);
        var rejected = await harness.Service.ImportAsync(changed, bytes);
        Assert.False(rejected.Succeeded);
        Assert.Null(rejected.Draft);
        Assert.Equal(drafts, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        Assert.Equal(events, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events;"));
    }

    /// <summary>
    /// The transfer policy grants only the transfer permissions under test, plus the
    /// abandonment permission when a case exercises a real lifecycle transition. The
    /// persisted Development policy keeps its original bytes and hash; this explicit
    /// policy never edits it.
    /// </summary>
    private static AuthorizationPolicy TransferAuthorizationPolicy(bool lifecycle = false)
    {
        var transferPermissions = new[]
        {
            Permission.ManageRecipeTrustStore, Permission.ManageRecipeSigningKeys,
            Permission.ReleaseRecipe,
            Permission.ImportRecipe, Permission.ExportRecipe
        };
        var roles = RecipeDraftTestPolicies.Authoring.RoleBundles.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == HumanRoleBundle.Administrator
                ? pair.Value.Concat(transferPermissions)
                    .Concat(lifecycle ? new[] { Permission.AbandonRecipeDraft } : Array.Empty<Permission>())
                    .Distinct()
                : pair.Value.AsEnumerable());
        var stepUp = RecipeDraftTestPolicies.Authoring.StepUpPermissions
            .Concat(new[] { Permission.ImportRecipe, Permission.ExportRecipe })
            .Distinct();
        return new AuthorizationPolicy("recipe-transfer-tests", "v138-runtime", roles, stepUp);
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task V138_R15_ImportedDraftKeepsOriginAcrossLocalEditAndOrdinaryRelease()
    {
        await using var harness = await TransferHarness.CreateAsync(includeRelease: true);
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        var imported = await harness.ImportAsync(exported.Export!.ToArray());
        Assert.True(imported.Succeeded, imported.Outcome.ReasonCode);
        var source = imported.Draft!.Content;
        var edited = new RecipeDraftContent(source.RecipeKey, "本地工程师修订", source.Algorithm,
            source.Configuration, source.CameraRole, source.Camera, source.AlgorithmExecutionTimeout,
            source.AssetRequirements, source.PolicyRequirements, source.ValueOrigins,
            source.CameraProviderExtension, source.CalibrationRequirements, source.PartIdentityRequirement);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(edited, out var document, out var reason), reason);
        var saved = await harness.Fixture.SaveAsync(Guid.NewGuid(), imported.Draft.DraftId, 1,
            imported.Draft.RevisionContentHash, document!, "本地编辑导入草稿");
        Assert.True(saved.Saved, saved.ReasonCode);
        Assert.Equal(2, saved.Revision!.Revision);
        Assert.Equal(imported.Draft.Content.RecipeKey, saved.Revision.Content.RecipeKey);
        var origin = await harness.Service.ReadImportAsync(imported.Draft.DraftId);
        Assert.True(origin.Available, origin.ReasonCode);
        Assert.Equal(imported.Import, origin.Provenance);

        var validationsBeforeRelease = harness.Factory!.ValidationCalls;
        var released = await harness.ReleaseSourceAsync(saved.Revision);
        Assert.True(harness.Factory.ValidationCalls > validationsBeforeRelease);
        Assert.Equal(saved.Revision.RevisionContentHash, released.Record.Source.RevisionContentHash);
        Assert.Equal(saved.Revision.DraftId, released.Record.Source.DraftId);
        Assert.Equal(saved.Revision.AuthorPrincipalId, released.Record.ApproverPrincipalId);
        Assert.Equal(imported.Import, (await harness.Service.ReadImportAsync(imported.Draft.DraftId)).Provenance);
    }

    [Fact]
    public async Task V138_R16_CancellationAfterAuthorizationReleasesTheReservedGuardWithoutWriting()
    {
        await using var harness = await TransferHarness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var guard = new TransferGuardProbe();
        var command = new ReplaceRecipeTrustStoreCommand(Guid.NewGuid(), harness.Fixture.Invocation(),
            0, Array.Empty<RecipeTrustedSigner>(), "cancel immediately after authorization");
        await harness.Fixture.WaitForVerifiedAsync();
        var auditBefore = harness.Fixture.Scalar("SELECT COUNT(*) FROM audit_entries;");
        try
        {
            var result = await harness.Fixture.Store.ExecuteRecipeTransferAsync(command,
                new RecipeTransferPreparedOperation(command), (_, _, _) =>
                {
                    cancellation.Cancel();
                    return new RecipeTransferEvaluation(
                        new RecipeTransferResult(new(command.CorrelationId, CommandDisposition.Accepted,
                            "RecipeTransferAuthorized", AuditPersistence.Persisted)),
                        Array.Empty<IdentityAuditEvent>(), null, guard, null);
                }, new StoreDeadline(harness.Fixture.Options.CommitTimeout), cancellation.Token);
            Assert.False(result.Succeeded);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        await guard.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(guard.Committed);
        Assert.Equal(0, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_trust_versions;"));
        Assert.Equal(0, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events;"));
        Assert.Equal(1, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        Assert.Equal(auditBefore, harness.Fixture.Scalar("SELECT COUNT(*) FROM audit_entries;"));
    }

    [Fact]
    public async Task V138_R17_ExportReplayRejectsUnavailableOriginalArtifactWithoutAnotherMutation()
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        var source = new RecipeTransferSourceSelection(new RecipeDraftRevisionReference(
            harness.SourceRevision.DraftId, harness.SourceRevision.Revision, harness.SourceRevision.RevisionContentHash));
        var bare = new ExportRecipeTransferCommand(Guid.NewGuid(), harness.Fixture.Invocation(), source,
            key.KeyId, key.Signer.PublicKeyFingerprint, "export with lost response");
        var stepUp = await harness.Fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
            bare.CorrelationId, bare.Invocation, new StepUpBinding(Permission.ExportRecipe,
                bare.CorrelationId, bare.AuthorizationTarget, AuditedCommandKind.ExportRecipeTransfer),
            harness.Fixture.Password));
        Assert.True(stepUp.Succeeded, stepUp.ReasonCode);
        var command = bare with { Invocation = harness.Fixture.Invocation(stepUp.GrantId) };
        var first = await harness.Service.ExportAsync(command);
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        Assert.NotNull(first.Export);
        await harness.Fixture.WaitForVerifiedAsync();
        var tables = new[] { "audit_entries", "command_facts", "recipe_transfer_events",
            "recipe_transfer_signing_keys", "recipe_transfer_trust_versions",
            "recipe_transfer_import_provenance", "recipe_draft_revisions" };
        var counts = tables.Select(table => harness.Fixture.Scalar($"SELECT COUNT(*) FROM {table};")).ToArray();
        var identityRevision = harness.Fixture.Scalar("SELECT Revision FROM identity_authority WHERE Id=1;");
        var replay = await harness.Service.ExportAsync(command);
        Assert.False(replay.Succeeded);
        Assert.Equal("RecipeTransferReplayResultUnavailable", replay.Outcome.ReasonCode);
        Assert.Null(replay.Export);
        Assert.Equal(counts, tables.Select(table => harness.Fixture.Scalar($"SELECT COUNT(*) FROM {table};")).ToArray());
        Assert.Equal(identityRevision, harness.Fixture.Scalar("SELECT Revision FROM identity_authority WHERE Id=1;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V138_R18_SigningKeyIdentityCannotBeCreatedAgainAfterCreationOrRetirement(bool retired)
    {
        await using var harness = await TransferHarness.CreateAsync();
        var key = await harness.CreateSigningKeyAsync();
        if (retired)
        {
            var retirement = await harness.RetireSigningKeyAsync(key);
            Assert.True(retirement.Succeeded, retirement.Outcome.ReasonCode);
        }
        var bare = new CreateRecipeSigningKeyCommand(Guid.NewGuid(), harness.Fixture.Invocation(),
            key.KeyId, key.Signer.Scope, key.Signer.NotBeforeUtc, key.Signer.NotAfterUtc,
            "attempt to reuse immutable key identity");
        var stepUp = await harness.Fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
            bare.CorrelationId, bare.Invocation, new StepUpBinding(Permission.ManageRecipeSigningKeys,
                bare.CorrelationId, bare.AuthorizationTarget, AuditedCommandKind.CreateRecipeSigningKey),
            harness.Fixture.Password));
        Assert.True(stepUp.Succeeded, stepUp.ReasonCode);
        await harness.Fixture.WaitForVerifiedAsync();
        var auditBefore = harness.Fixture.Scalar("SELECT COUNT(*) FROM audit_entries;");
        var eventsBefore = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events;");
        var result = await harness.Service.CreateSigningKeyAsync(bare with
            { Invocation = harness.Fixture.Invocation(stepUp.GrantId) });
        Assert.False(result.Succeeded);
        Assert.Equal("RecipeTransferSigningKeyAlreadyExists", result.Outcome.ReasonCode);
        Assert.Equal(auditBefore, harness.Fixture.Scalar("SELECT COUNT(*) FROM audit_entries;"));
        Assert.Equal(eventsBefore, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events;"));
        var keys = await harness.Service.ReadSigningKeysAsync();
        Assert.True(keys.Available, keys.ReasonCode);
        var current = Assert.Single(keys.Keys);
        Assert.Equal(key.KeyId, current.KeyId);
        Assert.Equal(key.Signer, current.Signer);
        Assert.Equal(retired, current.Retired);
    }

    [Theory]
    [InlineData("SourceStationId")]
    [InlineData("SourceDescriptorContentHash")]
    public async Task V138_R19_ExternalSourceStationSurvivesColdImportAndCannotBeAltered(string tamperedField)
    {
        await using var harness = await TransferHarness.CreateAsync();
        using var externalKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var signer = new RecipeTrustedSigner("external-provenance-key",
            Convert.ToBase64String(externalKey.ExportSubjectPublicKeyInfo()), "recipe-data",
            now.AddMinutes(-1), now.AddHours(1));
        await harness.ReplaceTrustAsync(signer);
        Assert.True(RecipeTransferContentCodec.TryEncode(harness.SourceRevision.Content, harness.PortablePolicy,
            out var recipe, out var projection, out var reason), reason);
        var source = new RecipeTransferSource(Guid.NewGuid(), "external.recipe", 7,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("external source revision"))),
            RecipeTransferSourceLifecycle.Retired, "external-station-17");
        var manifest = new RecipeTransferPackageManifest(Guid.NewGuid(), source, now,
            new(signer.KeyId, signer.Scope), RecipeTransferContentCodec.Dependencies(projection!),
            new[] { new RecipeTransferMemberManifest(RecipeTransferPackageLimits.RecipeMemberPath,
                recipe!.Length, Convert.ToHexString(SHA256.HashData(recipe))) },
            RecipeTransferPackageManifest.ZeroContentHash);
        manifest = RecipeTransferPackageCodec.FinalizeManifest(manifest, recipe!);
        var bytes = RecipeTransferPackageCodec.Encode(manifest, recipe!, externalKey.SignData(
            RecipeTransferPackageCodec.GetSignatureInput(manifest), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var imported = await harness.ImportAsync(bytes);
        Assert.True(imported.Succeeded, imported.Outcome.ReasonCode);
        await harness.Fixture.WaitForVerifiedAsync();
        var query = new SqliteRecipeTransferQuery(harness.Fixture.Options);
        var cold = await query.ReadImportAsync(imported.Draft!.DraftId);
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.Equal(source.SourceStationId, cold.Provenance!.SourceStationId);
        Assert.Equal(source.ContentHash, cold.Provenance.SourceDescriptorContentHash);
        Assert.Equal(source.Lifecycle.ToString(), cold.Provenance.SourceLifecycle);
        Assert.False(imported.Draft.Published);
        Assert.False(imported.Draft.Active);
        Assert.Equal(0, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events WHERE Kind=4;"));

        await ExecuteSqlAsync(harness.Fixture.Options.DatabasePath,
            "DROP TRIGGER recipe_transfer_import_immutable_update;");
        try
        {
            await ExecuteSqlAsync(harness.Fixture.Options.DatabasePath,
                $"UPDATE recipe_transfer_import_provenance SET {tamperedField}=$value WHERE DraftId=$draft;",
                ("$value", tamperedField == "SourceStationId" ? "altered-station" : new string('A', 64)),
                ("$draft", imported.Draft.DraftId.ToString("D")));
        }
        finally
        {
            await ExecuteSqlAsync(harness.Fixture.Options.DatabasePath, @"
                CREATE TRIGGER recipe_transfer_import_immutable_update
                BEFORE UPDATE ON recipe_transfer_import_provenance BEGIN
                    SELECT RAISE(ABORT,'ImmutableRecipeTransferImport');
                END;");
        }
        Assert.False((await query.ReadImportAsync(imported.Draft.DraftId)).Available);
        Assert.False((await new SqliteRecipeDraftQuery(harness.Fixture.Options).ReadAsync(imported.Draft.DraftId)).Available);
    }

    [Fact]
    public async Task V148_T01_RetiredReleaseExportsItsRealLifecycleAndImportsAsDraftOnly()
    {
        await using var harness = await TransferHarness.CreateAsync(includeRelease: true, lifecycle: true);
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var released = await harness.ReleaseSourceAsync();
        var retired = await harness.RetireAsync(released);
        Assert.Equal(RecipeLifecycleKind.ReleasedRetired, retired.Record!.Kind);
        Assert.Null(retired.Record.ClearedActive);
        var state = await harness.LifecycleHistory.ReadReleaseAsync(released.Reference,
            released.Record.ReleaseId, released.Record.ContentHash);
        Assert.True(state.Available, state.ReasonCode);
        Assert.Equal(ReleasedRecipeLifecycleState.Retired, state.State);

        // A Retired Recipe stays export material: the package must not claim it is available.
        var exported = await harness.ExportSelectionAsync(new RecipeTransferSourceSelection(
            released.Reference, released.Record.ReleaseId, released.Record.ContentHash));
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        Assert.True(RecipeTransferPackageCodec.TryRead(exported.Export!.ToArray(), out var package,
            out var packageReason), packageReason);
        Assert.Equal(RecipeTransferSourceLifecycle.Retired, package!.Manifest.Source.Lifecycle);
        Assert.Equal(released.Record.ReleaseId, package.Manifest.Source.SourceId);
        Assert.Equal(released.Record.ContentHash, package.Manifest.Source.RevisionContentHash);

        var releasesBefore = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_release_events;");
        var imported = await harness.ImportAsync(exported.Export.ToArray());
        Assert.True(imported.Succeeded, imported.Outcome.ReasonCode);
        Assert.Equal("Retired", imported.Import!.SourceLifecycle);
        Assert.Equal(released.Record.ReleaseId.ToString("D") + ":" + released.Reference.Id,
            imported.Import.SourceRecipeIdentity);
        Assert.NotEqual(released.Record.Source.DraftId, imported.Draft!.DraftId);
        Assert.Equal(1, imported.Draft.Revision);
        Assert.False(imported.Draft.Published);
        Assert.False(imported.Draft.Active);
        Assert.Null(imported.Draft.Content.MigrationLineage);
        Assert.Null(imported.Draft.Content.LifecycleLineage);
        Assert.Equal(releasesBefore, harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_release_events;"));
        await harness.Fixture.WaitForVerifiedAsync();
        var cold = await new SqliteRecipeTransferQuery(harness.Fixture.Options)
            .ReadImportAsync(imported.Draft.DraftId);
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.Equal("Retired", cold.Provenance!.SourceLifecycle);
        Assert.Equal(RecipeDraftLifecycleState.Open,
            (await harness.LifecycleHistory.ReadDraftAsync(imported.Draft.DraftId)).State);
        Assert.Equal(ReleasedRecipeLifecycleState.Retired,
            (await harness.LifecycleHistory.ReadReleaseAsync(released.Reference, released.Record.ReleaseId,
                released.Record.ContentHash)).State);
    }

    [Fact]
    public async Task V148_T02_AbandonedDraftExportsItsRealLifecycleAndImportsAsDraftOnly()
    {
        await using var harness = await TransferHarness.CreateAsync(lifecycle: true);
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var abandoned = await harness.AbandonAsync(harness.SourceRevision);
        Assert.Equal(RecipeLifecycleKind.DraftAbandoned, abandoned.Record!.Kind);
        var state = await harness.LifecycleHistory.ReadDraftAsync(harness.SourceRevision.DraftId);
        Assert.True(state.Available, state.ReasonCode);
        Assert.Equal(RecipeDraftLifecycleState.Abandoned, state.State);

        // The preserved revision stays readable history and may still be exported as such.
        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        Assert.True(RecipeTransferPackageCodec.TryRead(exported.Export!.ToArray(), out var package,
            out var packageReason), packageReason);
        Assert.Equal(RecipeTransferSourceLifecycle.Abandoned, package!.Manifest.Source.Lifecycle);
        Assert.Equal(harness.SourceRevision.DraftId, package.Manifest.Source.SourceId);
        Assert.Equal(harness.SourceRevision.RevisionContentHash, package.Manifest.Source.RevisionContentHash);

        var imported = await harness.ImportAsync(exported.Export.ToArray());
        Assert.True(imported.Succeeded, imported.Outcome.ReasonCode);
        Assert.Equal("Abandoned", imported.Import!.SourceLifecycle);
        Assert.Equal(harness.SourceRevision.DraftId.ToString("D") + ":" + harness.SourceRevision.Content.RecipeKey,
            imported.Import.SourceRecipeIdentity);
        Assert.NotEqual(harness.SourceRevision.DraftId, imported.Draft!.DraftId);
        Assert.Equal(1, imported.Draft.Revision);
        Assert.False(imported.Draft.Published);
        Assert.False(imported.Draft.Active);
        Assert.Null(imported.Draft.Content.LifecycleLineage);
        await harness.Fixture.WaitForVerifiedAsync();
        Assert.Equal(RecipeDraftLifecycleState.Open,
            (await harness.LifecycleHistory.ReadDraftAsync(imported.Draft.DraftId)).State);
        Assert.Equal(RecipeDraftLifecycleState.Abandoned,
            (await harness.LifecycleHistory.ReadDraftAsync(harness.SourceRevision.DraftId)).State);
    }

    [Fact]
    public async Task V148_T03_RetirementBetweenPrepareAndExportIsRefusedWithoutPackageOrAudit()
    {
        await using var harness = await TransferHarness.CreateAsync(includeRelease: true, lifecycle: true);
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var released = await harness.ReleaseSourceAsync();
        var exported = await harness.ExportSelectionAsync(new RecipeTransferSourceSelection(
            released.Reference, released.Record.ReleaseId, released.Record.ContentHash));
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        var bytes = exported.Export!.ToArray();
        Assert.True(RecipeTransferPackageCodec.TryRead(bytes, out var package, out var packageReason),
            packageReason);
        Assert.Equal(RecipeTransferSourceLifecycle.Released, package!.Manifest.Source.Lifecycle);

        // The exact retirement lands after the package bytes were prepared.
        await harness.RetireAsync(released);
        var exportsBefore = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events WHERE Kind=4;");

        // Re-enter the real authorization and writer boundary with the same prepared
        // artifact. The frozen claim still says Released, so the stale lifecycle must be
        // refused before any package or audit binding is emitted.
        var selection = new RecipeTransferSourceSelection(released.Reference, released.Record.ReleaseId,
            released.Record.ContentHash);
        var correlation = Guid.NewGuid();
        var bare = new ExportRecipeTransferCommand(correlation, harness.Fixture.Invocation(), selection,
            key.KeyId, key.Signer.PublicKeyFingerprint, "retirement between prepare and export");
        var grant = await harness.IssueGrantAsync(bare.CorrelationId, bare.AuthorizationTarget,
            Permission.ExportRecipe, AuditedCommandKind.ExportRecipeTransfer);
        var command = bare with { Invocation = harness.Fixture.Invocation(grant) };
        var frozen = new RecipeTransferSource(released.Record.ReleaseId, released.Reference.Id,
            released.Record.RecipeVersion, released.Record.ContentHash, RecipeTransferSourceLifecycle.Released,
            harness.Fixture.Options.LocalIdentity!.StationId);
        var prepared = new RecipeTransferPreparedOperation(command, bytes, package, signer: key.Signer,
            frozenSource: frozen, frozenSourceContentHash: released.Content.ContentHash,
            frozenKeyFingerprint: key.Signer.PublicKeyFingerprint);
        var refused = await harness.Fixture.Authorization.ExecuteRecipeTransferAsync(command, prepared, null,
            Guid.NewGuid(), new StoreDeadline(harness.Fixture.Options.CommitTimeout), CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(CommandDisposition.Rejected, refused.Outcome.Disposition);
        Assert.Equal("RecipeTransferExportLifecycleChanged", refused.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Unavailable, refused.Outcome.Audit);
        Assert.Null(refused.Export);
        Assert.Equal(exportsBefore,
            harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events WHERE Kind=4;"));
        Assert.Equal(0, harness.Fixture.Scalar(
            $"SELECT COUNT(*) FROM recipe_transfer_events WHERE OperationId='{correlation:D}';"));
        Assert.Equal(0, harness.Fixture.Scalar(
            $"SELECT COUNT(*) FROM command_facts WHERE CorrelationId='{correlation:D}';"));

        // The package that legitimately claimed Released before the retirement stays valid
        // history: it is never re-evaluated against the later lifecycle state.
        var history = await new SqliteRecipeTransferQuery(harness.Fixture.Options)
            .QueryAsync(new RecipeTransferFilter(PageSize: 32));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Contains(history.Records, item => item.Kind == RecipeTransferEventKind.Exported);
    }

    [Fact]
    public async Task V148_T04_AbandonmentBetweenPrepareAndExportIsRefusedWithoutPackageOrAudit()
    {
        await using var harness = await TransferHarness.CreateAsync(lifecycle: true);
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        var exported = await harness.ExportAsync();
        Assert.True(exported.Succeeded, exported.Outcome.ReasonCode);
        var bytes = exported.Export!.ToArray();
        Assert.True(RecipeTransferPackageCodec.TryRead(bytes, out var package, out var packageReason),
            packageReason);
        Assert.Equal(RecipeTransferSourceLifecycle.Draft, package!.Manifest.Source.Lifecycle);

        // The abandonment lands after the package bytes were prepared.
        await harness.AbandonAsync(harness.SourceRevision);
        var exportsBefore = harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events WHERE Kind=4;");

        var source = new RecipeTransferSourceSelection(new RecipeDraftRevisionReference(
            harness.SourceRevision.DraftId, harness.SourceRevision.Revision,
            harness.SourceRevision.RevisionContentHash));
        var correlation = Guid.NewGuid();
        var bare = new ExportRecipeTransferCommand(correlation, harness.Fixture.Invocation(), source,
            key.KeyId, key.Signer.PublicKeyFingerprint, "abandonment between prepare and export");
        var grant = await harness.IssueGrantAsync(bare.CorrelationId, bare.AuthorizationTarget,
            Permission.ExportRecipe, AuditedCommandKind.ExportRecipeTransfer);
        var command = bare with { Invocation = harness.Fixture.Invocation(grant) };
        var frozen = new RecipeTransferSource(harness.SourceRevision.DraftId,
            harness.SourceRevision.Content.RecipeKey, harness.SourceRevision.Revision,
            harness.SourceRevision.RevisionContentHash, RecipeTransferSourceLifecycle.Draft,
            harness.Fixture.Options.LocalIdentity!.StationId);
        var prepared = new RecipeTransferPreparedOperation(command, bytes, package, signer: key.Signer,
            frozenSource: frozen, frozenSourceContentHash: harness.SourceRevision.Content.ContentHash,
            frozenKeyFingerprint: key.Signer.PublicKeyFingerprint);
        var refused = await harness.Fixture.Authorization.ExecuteRecipeTransferAsync(command, prepared, null,
            Guid.NewGuid(), new StoreDeadline(harness.Fixture.Options.CommitTimeout), CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal("RecipeTransferExportLifecycleChanged", refused.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Unavailable, refused.Outcome.Audit);
        Assert.Null(refused.Export);
        Assert.Equal(exportsBefore,
            harness.Fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events WHERE Kind=4;"));
        Assert.Equal(0, harness.Fixture.Scalar(
            $"SELECT COUNT(*) FROM command_facts WHERE CorrelationId='{correlation:D}';"));
    }

    [Fact]
    public async Task V148_T05_ConfiguredLifecycleKeepsOpenDraftAndAvailableReleaseLabels()
    {
        await using var harness = await TransferHarness.CreateAsync(includeRelease: true, lifecycle: true);
        var key = await harness.CreateSigningKeyAsync();
        await harness.ReplaceTrustAsync(key.Signer);
        Assert.Equal(RecipeDraftLifecycleState.Open,
            (await harness.LifecycleHistory.ReadDraftAsync(harness.SourceRevision.DraftId)).State);

        var draftExport = await harness.ExportAsync();
        Assert.True(draftExport.Succeeded, draftExport.Outcome.ReasonCode);
        Assert.True(RecipeTransferPackageCodec.TryRead(draftExport.Export!.ToArray(), out var draftPackage,
            out var draftReason), draftReason);
        Assert.Equal(RecipeTransferSourceLifecycle.Draft, draftPackage!.Manifest.Source.Lifecycle);

        var released = await harness.ReleaseSourceAsync();
        Assert.Equal(ReleasedRecipeLifecycleState.Available, (await harness.LifecycleHistory.ReadReleaseAsync(
            released.Reference, released.Record.ReleaseId, released.Record.ContentHash)).State);
        var releaseExport = await harness.ExportSelectionAsync(new RecipeTransferSourceSelection(
            released.Reference, released.Record.ReleaseId, released.Record.ContentHash));
        Assert.True(releaseExport.Succeeded, releaseExport.Outcome.ReasonCode);
        Assert.True(RecipeTransferPackageCodec.TryRead(releaseExport.Export!.ToArray(), out var releasePackage,
            out var releaseReason), releaseReason);
        Assert.Equal(RecipeTransferSourceLifecycle.Released, releasePackage!.Manifest.Source.Lifecycle);

        var imported = await harness.ImportAsync(releaseExport.Export.ToArray());
        Assert.True(imported.Succeeded, imported.Outcome.ReasonCode);
        Assert.Equal("Released", imported.Import!.SourceLifecycle);
        Assert.False(imported.Draft!.Published);
        Assert.False(imported.Draft.Active);
        Assert.Null(imported.Draft.Content.LifecycleLineage);
    }

    private sealed class TransferGuardProbe : IIdentityTransactionGuard
    {
        internal TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Committed { get; private set; }
        public void Commit() => Committed = true;
        public void Dispose() => Disposed.TrySetResult(true);
    }

    private sealed class TransferHarness : IAsyncDisposable
    {
        private TransferHarness(RecipeDraftStorageTests.Fixture fixture,
            AlgorithmDescriptor descriptor, RecipeDraftRevision sourceRevision,
            RecipeTransferPortablePolicy portablePolicy,
            IRecipeTransferService service, RecipeDraftService? drafts,
            StationRuntime? runtime, RecipeReleaseService? releases, TransferFactory? factory)
        {
            Fixture = fixture;
            Descriptor = descriptor;
            SourceRevision = sourceRevision;
            PortablePolicy = portablePolicy;
            Service = service;
            Drafts = drafts;
            Runtime = runtime;
            Releases = releases;
            Factory = factory;
        }

        internal RecipeDraftStorageTests.Fixture Fixture { get; }
        internal AlgorithmDescriptor Descriptor { get; }
        internal RecipeDraftRevision SourceRevision { get; }
        internal RecipeTransferPortablePolicy PortablePolicy { get; }
        internal IRecipeTransferService Service { get; }
        internal RecipeDraftService? Drafts { get; }
        internal StationRuntime? Runtime { get; }
        internal RecipeReleaseService? Releases { get; }
        internal TransferFactory? Factory { get; }
        private RecipeSigningKeyRecord? _signingKey;

        internal static async Task<TransferHarness> CreateAsync(bool includeRelease = false,
            int maximumTransfers = 1000, bool lifecycle = false)
        {
            var seed = RecipeTransferContentCodecTests.Fixture();
            var transferOptions = new RecipeTransferStoreOptions
            { PortablePolicy = seed.Policy, MaximumEntries = maximumTransfers };
            var releaseOptions = includeRelease
                ? new RecipeReleaseStoreOptions(new("V138.Transfer.Release", "1",
                    RecipeGovernanceMode.SingleApproverRelease))
                : null;
            var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
                authorizationPolicy: TransferAuthorizationPolicy(lifecycle), recipeReleases: releaseOptions,
                recipeTransfers: transferOptions,
                recipeLifecycle: lifecycle ? new RecipeLifecycleStoreOptions() : null);
            RecipeDraftService? drafts = null;
            StationRuntime? runtime = null;
            RecipeReleaseService? releases = null;
            TransferFactory? factory = null;
            try
            {
                var content = WithLocalExecutionPolicy(fixture, seed.Content);
                Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
                var draftId = Guid.NewGuid();
                var saved = await fixture.SaveAsync(Guid.NewGuid(), draftId, 0, null, document!,
                    "transfer source draft");
                Assert.True(saved.Saved, saved.ReasonCode);
                IReleasedRecipeQuery? releaseQuery = null;
                if (includeRelease)
                {
                    factory = new TransferFactory(seed.Descriptor);
                    drafts = new(new[] { factory }, fixture.Options, fixture.Authorization,
                        new SqliteRecipeDraftQuery(fixture.Options));
                    runtime = new(fixture.Store, TimeSpan.FromMilliseconds(500), fixture.Sessions,
                        fixture.Authorization);
                    releaseQuery = new SqliteReleasedRecipeQuery(fixture.Options);
                    releases = new(drafts, fixture.Authorization, releaseQuery, fixture.Options,
                        () => runtime.GetSnapshotAsync());
                    runtime.ConfigureRecipeReleaseService(releases);
                }
                var service = new RecipeTransferService(fixture.Options, fixture.Authorization, fixture.Store,
                    new SqliteRecipeDraftQuery(fixture.Options), releaseQuery,
                    new SqliteRecipeTransferQuery(fixture.Options), new[] { seed.Descriptor });
                return new TransferHarness(fixture, seed.Descriptor, saved.Revision!, seed.Policy, service,
                    drafts, runtime, releases, factory);
            }
            catch
            {
                if (runtime is not null) await runtime.DisposeAsync();
                if (drafts is not null) await drafts.DisposeAsync();
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal async Task<ReleasedRecipe> ReleaseSourceAsync(RecipeDraftRevision? source = null)
        {
            Assert.NotNull(Releases);
            Assert.NotNull(Fixture.Options.RecipeReleases);
            source ??= SourceRevision;
            var bare = new ReleaseRecipeCommand(Guid.NewGuid(), Fixture.Invocation(),
                source.DraftId, source.Revision, source.RevisionContentHash,
                Fixture.Options.RecipeReleases!.Policy.Reference, "release transfer source");
            var stepUp = await Fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
                bare.CorrelationId, bare.Invocation,
                new StepUpBinding(Permission.ReleaseRecipe, bare.CorrelationId,
                    bare.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe), Fixture.Password));
            Assert.True(stepUp.Succeeded, stepUp.ReasonCode);
            await Fixture.WaitForVerifiedAsync();
            var result = await Releases!.ReleaseAsync(bare with
            {
                Invocation = Fixture.Invocation(stepUp.GrantId)
            });
            Assert.True(result.Outcome.Disposition == CommandDisposition.Accepted,
                result.Outcome.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
            Assert.NotNull(result.Recipe);
            await Fixture.WaitForVerifiedAsync();
            return result.Recipe!;
        }

        internal async Task<RecipeSigningKeyRecord> CreateSigningKeyAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var correlation = Guid.NewGuid();
            var bare = new CreateRecipeSigningKeyCommand(correlation, Fixture.Invocation(),
                "transfer-key-" + correlation.ToString("N")[..8], "recipe-data",
                now.AddMinutes(-1), now.AddHours(1), "create transfer signing key");
            var grant = await GrantAsync(bare, Permission.ManageRecipeSigningKeys,
                AuditedCommandKind.CreateRecipeSigningKey);
            var command = new CreateRecipeSigningKeyCommand(correlation, Fixture.Invocation(grant),
                bare.KeyId, bare.Scope, bare.NotBeforeUtc, bare.NotAfterUtc, bare.Reason);
            var result = await Service.CreateSigningKeyAsync(command);
            Assert.True(result.Succeeded, result.Outcome.ReasonCode);
            _signingKey = result.SigningKey!;
            return result.SigningKey!;
        }

        internal async Task<RecipeTransferResult> ReplaceTrustAsync(
            params RecipeTrustedSigner[] signers)
        {
            var current = await Service.ReadTrustAsync();
            var expectedVersion = current.Trust?.Version ?? 0;
            var correlation = Guid.NewGuid();
            var bare = new ReplaceRecipeTrustStoreCommand(correlation, Fixture.Invocation(),
                expectedVersion, signers, "replace transfer trust");
            var grant = await GrantAsync(bare, Permission.ManageRecipeTrustStore,
                AuditedCommandKind.ReplaceRecipeTrustStore);
            var command = new ReplaceRecipeTrustStoreCommand(correlation, Fixture.Invocation(grant),
                expectedVersion, signers, bare.Reason);
            var result = await Service.ReplaceTrustAsync(command);
            Assert.True(result.Succeeded, result.Outcome.ReasonCode);
            return result;
        }

        internal async Task<RecipeTransferResult> ExportAsync(bool issueStepUp = true)
        {
            var source = new RecipeTransferSourceSelection(new RecipeDraftRevisionReference(
                SourceRevision.DraftId, SourceRevision.Revision, SourceRevision.RevisionContentHash));
            return await ExportSelectionAsync(source, issueStepUp);
        }

        internal async Task<RecipeTransferResult> ExportSelectionAsync(
            RecipeTransferSourceSelection source, bool issueStepUp = true)
        {
            var keys = await Service.ReadSigningKeysAsync();
            var key = _signingKey ?? Assert.Single(keys.Keys, item => !item.Retired);
            var correlation = Guid.NewGuid();
            var bare = new ExportRecipeTransferCommand(correlation, Fixture.Invocation(), source,
                key.KeyId, key.Signer.PublicKeyFingerprint, "export transfer package");
            var grant = issueStepUp
                ? await GrantAsync(bare, Permission.ExportRecipe, AuditedCommandKind.ExportRecipeTransfer)
                : (Guid?)null;
            var command = new ExportRecipeTransferCommand(correlation, Fixture.Invocation(grant), source,
                key.KeyId, key.Signer.PublicKeyFingerprint, bare.Reason);
            return await Service.ExportAsync(command);
        }

        internal async Task<RecipeTransferResult> ImportAsync(byte[] bytes,
            bool issueStepUp = true, Guid? operationId = null)
        {
            var built = await BuildImportCommandAsync(bytes, issueStepUp, operationId);
            return await Service.ImportAsync(built.Command, bytes);
        }

        internal async Task<(ImportRecipeTransferCommand Command, Guid? StepUpGrantId)> BuildImportCommandAsync(
            byte[] bytes, bool issueStepUp = true, Guid? operationId = null,
            Guid? existingStepUpGrantId = null)
        {
            var correlation = operationId ?? Guid.NewGuid();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var bare = new ImportRecipeTransferCommand(correlation, Fixture.Invocation(), hash,
                "import transfer package");
            var grant = issueStepUp
                ? await GrantAsync(bare, Permission.ImportRecipe, AuditedCommandKind.ImportRecipeTransfer)
                : existingStepUpGrantId;
            return (new ImportRecipeTransferCommand(correlation, Fixture.Invocation(grant), hash,
                bare.Reason), grant);
        }

        internal async Task<RecipeTransferResult> RetireSigningKeyAsync(RecipeSigningKeyRecord key)
        {
            var correlation = Guid.NewGuid();
            var bare = new RetireRecipeSigningKeyCommand(correlation, Fixture.Invocation(), key.KeyId,
                key.Signer.PublicKeyFingerprint, "retire transfer signing key");
            var grant = await GrantAsync(bare, Permission.ManageRecipeSigningKeys,
                AuditedCommandKind.RetireRecipeSigningKey);
            var command = new RetireRecipeSigningKeyCommand(correlation, Fixture.Invocation(grant),
                bare.KeyId, bare.ExpectedPublicKeyFingerprint, bare.Reason);
            return await Service.RetireSigningKeyAsync(command);
        }

        private Task<Guid> GrantAsync(RecipeTransferCommand command, Permission permission,
            AuditedCommandKind kind) => IssueGrantAsync(command.CorrelationId, command.Invocation,
                command.AuthorizationTarget, permission, kind);

        internal Task<Guid> IssueGrantAsync(Guid correlationId, string authorizationTarget,
            Permission permission, AuditedCommandKind kind) => IssueGrantAsync(correlationId,
                Fixture.Invocation(), authorizationTarget, permission, kind);

        private async Task<Guid> IssueGrantAsync(Guid correlationId, CommandInvocation invocation,
            string authorizationTarget, Permission permission, AuditedCommandKind kind)
        {
            var binding = new StepUpBinding(permission, correlationId, authorizationTarget, kind);
            var issued = await Fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
                correlationId, invocation, binding, Fixture.Password));
            Assert.True(issued.Succeeded, issued.ReasonCode + " | integrity=" + Fixture.Store.Integrity?.ReasonCode);
            Assert.True(issued.GrantId.HasValue);
            return issued.GrantId!.Value;
        }

        internal IRecipeLifecycleHistoryQuery LifecycleHistory => new SqliteRecipeLifecycleQuery(Fixture.Options);

        /// <summary>
        /// Applies one irreversible abandonment through the real authorization boundary, a
        /// fresh expiry-bound Step-Up grant and the real schema-33 writer. Only the
        /// non-active path is used, so no Runtime quiescence lease is involved.
        /// </summary>
        internal async Task<RecipeLifecycleResult> AbandonAsync(RecipeDraftRevision source)
        {
            Assert.NotNull(Fixture.Options.RecipeLifecycle);
            var command = new AbandonRecipeDraftCommand(Guid.NewGuid(), Fixture.Invocation(), source.DraftId,
                source.Revision, source.RevisionContentHash, "V148 transfer source abandonment");
            var grant = await IssueGrantAsync(command.CorrelationId, command.AuthorizationTarget,
                Permission.AbandonRecipeDraft, AuditedCommandKind.AbandonRecipeDraft);
            return await ApplyLifecycleAsync(command with { Invocation = Fixture.Invocation(grant) });
        }

        /// <summary>
        /// Retires one exact non-active release through the same real authorization and
        /// writer boundary, so the transfer cases read genuine durable lifecycle history.
        /// </summary>
        internal async Task<RecipeLifecycleResult> RetireAsync(ReleasedRecipe release)
        {
            Assert.NotNull(Fixture.Options.RecipeLifecycle);
            var command = new RetireReleasedRecipeCommand(Guid.NewGuid(), Fixture.Invocation(),
                release.Record.Recipe, release.Record.ReleaseId, release.Record.ContentHash, null,
                "V148 transfer source retirement");
            var grant = await IssueGrantAsync(command.CorrelationId, command.AuthorizationTarget,
                Permission.RetireRecipe, AuditedCommandKind.RetireReleasedRecipe);
            return await ApplyLifecycleAsync(command with { Invocation = Fixture.Invocation(grant) });
        }

        private async ValueTask<RecipeLifecycleResult> ApplyLifecycleAsync(RuntimeCommand command)
        {
            var result = await Fixture.Authorization.ApplyRecipeLifecycleAsync(command, Guid.NewGuid(), null,
                null, null, new StoreDeadline(Fixture.Options.CommitTimeout), CancellationToken.None);
            Assert.Equal(CommandDisposition.Accepted, result.Outcome.Disposition);
            Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
            Assert.NotNull(result.Record);
            await Fixture.WaitForVerifiedAsync();
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (Runtime is not null) await Runtime.DisposeAsync();
            if (Drafts is not null) await Drafts.DisposeAsync();
            await Fixture.DisposeAsync();
        }

        private static RecipeDraftContent WithLocalExecutionPolicy(
            RecipeDraftStorageTests.Fixture fixture, RecipeDraftContent source)
        {
            var execution = fixture.Options.RecipeDrafts!.ExecutionPolicy;
            var requirement = new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                new RecipeContractReference(execution.Id, execution.Version, execution.ContentHash));
            return new RecipeDraftContent(source.RecipeKey, source.DisplayName, source.Algorithm,
                source.Configuration, source.CameraRole, source.Camera, source.AlgorithmExecutionTimeout,
                source.AssetRequirements, new[] { requirement }, source.ValueOrigins,
                source.CameraProviderExtension, source.CalibrationRequirements, source.PartIdentityRequirement);
        }
    }

    private sealed class TransferFactory : IVisionAlgorithmFactory
    {
        internal TransferFactory(AlgorithmDescriptor descriptor) => Descriptor = descriptor;

        public AlgorithmDescriptor Descriptor { get; }
        internal int ValidationCalls { get; private set; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            ValidationCalls++;
            return ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>());
        }

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("RecipeTransferRuntimeMustNotCreateAlgorithm");
    }
}
