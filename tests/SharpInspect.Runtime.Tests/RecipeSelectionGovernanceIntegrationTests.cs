using System.Buffers.Binary;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T46 human governance integration: the real physical-console authorization service,
/// the real station reservation and the real schema-31 SQLite writer decide one
/// immutable Selection Policy/Map revision. The station stays Recovery=Required and is
/// never armed, so these checks claim durable governance and identity evidence only and
/// never claim physical Runtime acceptance.
/// </summary>
public sealed class RecipeSelectionGovernanceIntegrationTests
{
    [Fact]
    public async Task V146_G01_AuthorizedConsoleChangePersistsExactImmutableMapAndIdentityRow()
    {
        await using var harness = await RecipeActivationServiceTests.ActivationHarness.CreateAsync(
            enableRecipeSelections: true);
        await harness.WaitForRecipeSelectionStartupAsync();

        // A fresh schema-31 deployment without a revision is exactly Local Operator Only.
        var initial = await harness.RecipeSelections.ReadCurrentAsync();
        Assert.True(initial.Available, initial.ReasonCode);
        Assert.Null(initial.Revision);
        Assert.Equal(RecipeSelectionMode.LocalOperatorOnly, initial.Mode);

        var released = harness.Released;
        var policy = RecipeSelectionIntegrationSupport.PlcPolicy("1");
        var map = RecipeSelectionIntegrationSupport.Map("1",
            RecipeSelectionIntegrationSupport.Entry(1, released));
        var command = new ChangeRecipeSelectionCommand(Guid.NewGuid(), harness.ActivationInvocation(), policy,
            map, null, "V146 enable the exact mapped PLC selection");
        var grant = await harness.IssueGrantAsync(Permission.ManageRecipeSelectionMap, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.ChangeRecipeSelection);

        var result = await harness.RecipeSelections.ChangeAsync(command with
        { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });

        Assert.True(result.Outcome.Disposition == CommandDisposition.Accepted, result.Outcome.ReasonCode);
        Assert.Equal("RecipeSelectionChanged", result.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        var revision = Assert.IsType<RecipeSelectionRevision>(result.Revision);
        Assert.Equal(1L, revision.Position);
        Assert.Null(revision.Previous);
        Assert.Equal(RecipeSelectionMode.PlcRequestedActivation, revision.Policy.Mode);
        Assert.Equal(policy.Reference, revision.Policy.Reference);
        Assert.Equal(map.Reference, revision.Map!.Reference);
        Assert.Equal(grant.GrantId, revision.StepUpGrantId);
        Assert.Equal(harness.Sessions.Current.PrincipalId, revision.ActorPrincipalId.ToString("D"));
        Assert.Equal(harness.Sessions.Current.SessionId, revision.ActorSessionId);

        // The map is stored exactly as proposed: the exact released recipe identity with
        // no name, position or latest alias resolution.
        var entry = Assert.Single(revision.Map.Entries);
        Assert.Equal(1u, entry.SelectionCode);
        Assert.Equal(released.Reference, entry.Recipe);
        Assert.Equal(released.Record.ReleaseId, entry.ReleaseId);
        Assert.Equal(released.Record.ContentHash, entry.ReleaseRecordContentHash);
        var validation = Assert.Single(revision.Validations);
        Assert.Equal(entry.Recipe, validation.Recipe);
        Assert.Equal(entry.ReleaseId, validation.ReleaseId);
        Assert.Equal(entry.ReleaseRecordContentHash, validation.ReleaseRecordContentHash);
        Assert.Equal(64, validation.ValidationContentHash.Length);

        // The durable revision is the only source for the read capability and the
        // authority snapshot.
        var current = await harness.RecipeSelections.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(revision.ContentHash, current.Revision!.ContentHash);
        Assert.Equal(RecipeSelectionMode.PlcRequestedActivation, current.Mode);
        var page = await harness.RecipeSelections.QueryAsync(new RecipeSelectionFilter());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(revision.ContentHash, Assert.Single(page.Revisions).ContentHash);
        Assert.Null(page.NextAfterPosition);

        // The persisted identity row is the exact ManageRecipeSelectionMap grant and it
        // stays a 49-field human envelope.
        await harness.WaitForVerifiedAsync();
        var row = await RecipeSelectionIntegrationSupport.SingleIdentityRowAsync(
            harness.Options, IdentityEventKind.RecipeSelectionChanged);
        var fields = RecipeSelectionIntegrationSupport.ReadIdentityPayloadFields(row.Payload);
        Assert.Equal(49, fields.Length);
        Assert.Equal("RecipeSelectionChanged", fields[2]);
        Assert.Equal(harness.Sessions.Current.PrincipalId, fields[30]);
        Assert.Equal(command.CorrelationId.ToString("D"), fields[31]);
        Assert.Equal(grant.GrantId!.Value.ToString("D"), fields[32]);
        Assert.Equal("ManageRecipeSelectionMap", fields[33]);
        Assert.Equal(command.AuthorizationTarget, fields[37]);
        Assert.Equal(command.CorrelationId.ToString("D"), fields[38]);
        Assert.Equal("ChangeRecipeSelection", fields[39]);
        Assert.True(IdentityAuditEvent.VerifyPayload(row.Payload, row.Ordinal,
            RecipeSelectionIntegrationSupport.StationId(harness.Options), RecipeSelectionStoreOptions.SchemaVersion) > 0);
    }

    [Fact]
    public async Task V146_G02_MissingStepUpIntegrationSourceAndMissingPermissionLeaveNoRevision()
    {
        await using var harness = await RecipeActivationServiceTests.ActivationHarness.CreateAsync(
            enableRecipeSelections: true);
        await harness.WaitForRecipeSelectionStartupAsync();
        var map = RecipeSelectionIntegrationSupport.Map("1",
            RecipeSelectionIntegrationSupport.Entry(1, harness.Released));

        // (1) The authorized physical console without a fresh Step-Up grant.
        var noGrant = new ChangeRecipeSelectionCommand(Guid.NewGuid(), harness.ActivationInvocation(),
            RecipeSelectionIntegrationSupport.PlcPolicy("1"), map, null, "V146 selection change without step-up");
        var rejected = await harness.RecipeSelections.ChangeAsync(noGrant);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("StepUpRequired", rejected.Outcome.ReasonCode);
        Assert.Null(rejected.Revision);

        // (2) A forged map command that only holds an integration session is never a
        // console action, even with a valid session.
        var forged = new ChangeRecipeSelectionCommand(Guid.NewGuid(), harness.Invocation(),
            RecipeSelectionIntegrationSupport.PlcPolicy("1"), map, null, "V146 forged integration map change");
        rejected = await harness.RecipeSelections.ChangeAsync(forged);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("PhysicalConsoleRequired", rejected.Outcome.ReasonCode);
        Assert.Null(rejected.Revision);

        // (3) A signed-in Technician bundle deliberately lacks ManageRecipeSelectionMap.
        await RecipeSelectionIntegrationSupport.CreateTechnicianAsync(harness);
        var technician = new ChangeRecipeSelectionCommand(Guid.NewGuid(), harness.ActivationInvocation(),
            RecipeSelectionIntegrationSupport.PlcPolicy("1"), map, null, "V146 technician map change");
        rejected = await harness.RecipeSelections.ChangeAsync(technician);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("PermissionDenied", rejected.Outcome.ReasonCode);
        Assert.Null(rejected.Revision);

        // No rejected attempt may publish a revision or leave the deployment non-default.
        Assert.Equal(0L, await RecipeSelectionIntegrationSupport.ScalarAsync(harness.Options,
            "SELECT COUNT(*) FROM recipe_selection_revisions;"));
        var current = await harness.RecipeSelections.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Revision);
        Assert.Equal(RecipeSelectionMode.LocalOperatorOnly, current.Mode);
        await harness.WaitForVerifiedAsync();
    }

    [Fact]
    public async Task V146_G03_AffectedUnionValidatesRemovedEntryAndRejectsWrongOrMissingTarget()
    {
        await using var harness = await RecipeActivationServiceTests.ActivationHarness.CreateAsync(
            enableRecipeSelections: true);
        await harness.WaitForRecipeSelectionStartupAsync();
        var first = harness.Released;
        var second = await RecipeSelectionIntegrationSupport.ReleaseAdditionalRecipeAsync(harness,
            "V146.Selection.Second", "V146 second selection target");

        var firstMap = RecipeSelectionIntegrationSupport.Map("1",
            RecipeSelectionIntegrationSupport.Entry(1, first),
            RecipeSelectionIntegrationSupport.Entry(2, second));
        var firstChange = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"), firstMap, null,
            "V146 map both exact selection targets");
        Assert.True(firstChange.Outcome.Disposition == CommandDisposition.Accepted, firstChange.Outcome.ReasonCode);
        var firstRevision = Assert.IsType<RecipeSelectionRevision>(firstChange.Revision);
        Assert.Equal(2, firstRevision.Validations.Count);
        Assert.Contains(firstRevision.Validations, value => value.ReleaseId == second.Record.ReleaseId);

        // The second change removes the second entry. The affected set is the full union
        // of the old and the new map, so the removed release is validated exactly like a
        // retained one and cannot survive unproved.
        var secondMap = RecipeSelectionIntegrationSupport.Map("2",
            RecipeSelectionIntegrationSupport.Entry(1, first));
        var secondChange = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"), secondMap, firstRevision.Reference,
            "V146 remove the second selection target");
        Assert.True(secondChange.Outcome.Disposition == CommandDisposition.Accepted, secondChange.Outcome.ReasonCode);
        var secondRevision = Assert.IsType<RecipeSelectionRevision>(secondChange.Revision);
        Assert.Equal(firstRevision.Reference, secondRevision.Previous);
        Assert.Equal(2, secondRevision.Validations.Count);
        Assert.Contains(secondRevision.Validations, value =>
            value.ReleaseId == second.Record.ReleaseId &&
            value.ReleaseRecordContentHash == second.Record.ContentHash);
        Assert.Single(secondRevision.Map!.Entries);
        Assert.DoesNotContain(secondRevision.Map.Entries, value => value.ReleaseId == second.Record.ReleaseId);

        // A wrong record hash and an unknown release identity never resolve to a target.
        var wrongHash = RecipeSelectionIntegrationSupport.Map("3",
            RecipeSelectionIntegrationSupport.Entry(1, first),
            new RecipeSelectionMapEntry(2, second.Reference, second.Record.ReleaseId, new string('A', 64)));
        var wrongHashResult = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"), wrongHash, secondRevision.Reference,
            "V146 wrong release record hash");
        Assert.Equal(CommandDisposition.Rejected, wrongHashResult.Outcome.Disposition);
        Assert.Equal("RecipeSelectionAffectedReleaseMismatch", wrongHashResult.Outcome.ReasonCode);
        Assert.Null(wrongHashResult.Revision);

        var missing = RecipeSelectionIntegrationSupport.Map("3",
            RecipeSelectionIntegrationSupport.Entry(1, first),
            new RecipeSelectionMapEntry(2, second.Reference, Guid.NewGuid(), second.Record.ContentHash));
        var missingResult = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"), missing, secondRevision.Reference,
            "V146 missing exact selection target");
        Assert.Equal(CommandDisposition.Rejected, missingResult.Outcome.Disposition);
        Assert.Equal("RecipeSelectionAffectedReleaseMissing", missingResult.Outcome.ReasonCode);
        Assert.Null(missingResult.Revision);

        // A stale expected revision is a conflict, never a silent overwrite.
        var stale = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"),
            RecipeSelectionIntegrationSupport.Map("3", RecipeSelectionIntegrationSupport.Entry(1, first)), null,
            "V146 stale expected revision");
        Assert.Equal(CommandDisposition.Rejected, stale.Outcome.Disposition);
        Assert.Equal("RecipeSelectionCurrentConflict", stale.Outcome.ReasonCode);
        Assert.Null(stale.Revision);

        // Exactly the two accepted revisions exist; every rejection left no trace.
        Assert.Equal(2L, await RecipeSelectionIntegrationSupport.ScalarAsync(harness.Options,
            "SELECT COUNT(*) FROM recipe_selection_revisions;"));
        var current = await harness.RecipeSelections.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(secondRevision.ContentHash, current.Revision!.ContentHash);
        await harness.WaitForVerifiedAsync();
    }

    [Fact]
    public async Task V146_G04_MapAndPolicyVersionReuseWithDifferentContentIsRejected()
    {
        await using var harness = await RecipeActivationServiceTests.ActivationHarness.CreateAsync(
            enableRecipeSelections: true);
        await harness.WaitForRecipeSelectionStartupAsync();
        var first = harness.Released;
        var second = await RecipeSelectionIntegrationSupport.ReleaseAdditionalRecipeAsync(harness,
            "V146.Selection.Other", "V146 other selection target");

        var change = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"),
            RecipeSelectionIntegrationSupport.Map("1", RecipeSelectionIntegrationSupport.Entry(1, first)), null,
            "V146 initial mapped selection");
        Assert.True(change.Outcome.Disposition == CommandDisposition.Accepted, change.Outcome.ReasonCode);
        var current = Assert.IsType<RecipeSelectionRevision>(change.Revision);

        // The same Map Id/Version cannot be re-published with another immutable body.
        var reusedMap = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"),
            RecipeSelectionIntegrationSupport.Map("1", RecipeSelectionIntegrationSupport.Entry(2, second)),
            current.Reference, "V146 map version reuse with other content");
        Assert.Equal(CommandDisposition.Rejected, reusedMap.Outcome.Disposition);
        Assert.Equal("RecipeSelectionMapVersionConflict", reusedMap.Outcome.ReasonCode);
        Assert.Null(reusedMap.Revision);

        // A changed policy body under the same Id/Version is the same conflict class.
        var reusedPolicy = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            new RecipeSelectionPolicy(RecipeSelectionIntegrationSupport.PolicyId, "1",
                RecipeSelectionMode.LocalOperatorOnly),
            null, current.Reference, "V146 policy version reuse with other content");
        Assert.Equal(CommandDisposition.Rejected, reusedPolicy.Outcome.Disposition);
        Assert.Equal("RecipeSelectionPolicyVersionConflict", reusedPolicy.Outcome.ReasonCode);
        Assert.Null(reusedPolicy.Revision);

        Assert.Equal(1L, await RecipeSelectionIntegrationSupport.ScalarAsync(harness.Options,
            "SELECT COUNT(*) FROM recipe_selection_revisions;"));
        var reread = await harness.RecipeSelections.ReadCurrentAsync();
        Assert.True(reread.Available, reread.ReasonCode);
        Assert.Equal(current.ContentHash, reread.Revision!.ContentHash);
        await harness.WaitForVerifiedAsync();
    }
}

/// <summary>
/// Shared bounded helpers for the T46 selection integration checks. They only build
/// immutable public contracts, read the real store and reuse the real activation
/// harness; they add no runtime provider and claim no physical acceptance.
/// </summary>
internal static class RecipeSelectionIntegrationSupport
{
    internal const string PolicyId = "V146.Selection.Policy";
    internal const string MapId = "V146.Selection.Map";

    internal static RecipeSelectionPolicy PlcPolicy(string version) =>
        new(PolicyId, version, RecipeSelectionMode.PlcRequestedActivation);

    internal static RecipeSelectionMap Map(string version, params RecipeSelectionMapEntry[] entries) =>
        new(MapId, version, entries);

    internal static RecipeSelectionMapEntry Entry(uint code, ReleasedRecipe released) =>
        new(code, released.Reference, released.Record.ReleaseId, released.Record.ContentHash);

    internal static string StationId(ProductionStoreOptions options) =>
        options.AuditIntegrityPolicy!.StationId;

    internal static async Task<RecipeSelectionChangeResult> ChangeAsync(
        RecipeActivationServiceTests.ActivationHarness harness, RecipeSelectionPolicy policy,
        RecipeSelectionMap? map, RecipeSelectionReference? expectedCurrent, string reason)
    {
        var command = new ChangeRecipeSelectionCommand(Guid.NewGuid(), harness.ActivationInvocation(), policy, map,
            expectedCurrent, reason);
        var grant = await harness.IssueGrantAsync(Permission.ManageRecipeSelectionMap, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.ChangeRecipeSelection);
        return await harness.RecipeSelections.ChangeAsync(command with
        { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });
    }

    /// <summary>Authors and releases one more exact recipe through the real services.</summary>
    internal static async Task<ReleasedRecipe> ReleaseAdditionalRecipeAsync(
        RecipeActivationServiceTests.ActivationHarness harness, string recipeKey, string displayName)
    {
        var template = harness.Released.Content;
        var descriptor = Assert.Single(harness.Drafts.Algorithms,
            value => value.Identity == template.Algorithm.Algorithm);
        var execution = harness.Options.RecipeDrafts!.ExecutionPolicy;
        var governance = harness.Options.RecipeReleases!.Policy;
        var content = new RecipeDraftContent(recipeKey, displayName,
            RecipeAlgorithmBinding.FromDescriptor(descriptor),
            AlgorithmConfigurationSnapshot.Create(descriptor.ConfigurationSchema,
                harness.Drafts.GetAuthoringDefaults(descriptor.Identity)),
            template.CameraRole, template.Camera, template.AlgorithmExecutionTimeout, null,
            new[]
            {
                new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                    new RecipeContractReference(execution.Id, execution.Version, execution.ContentHash)),
                new RecipePolicyRequirement(RecipePolicyKind.RecipeGovernance, governance.Reference)
            }, partIdentityRequirement: PartIdentityRequirement.None);
        var saved = await harness.Drafts.SaveAsync(new RecipeDraftSaveRequest(Guid.NewGuid(), Guid.NewGuid(), 0,
            null, content, "V146 author another exact selection target", harness.Invocation(), null));
        Assert.True(saved.Saved, saved.ReasonCode);
        var source = Assert.IsType<RecipeDraftRevision>(saved.Revision);
        await harness.WaitForVerifiedAsync();

        var command = new ReleaseRecipeCommand(Guid.NewGuid(), harness.Invocation(), source.DraftId,
            source.Revision, source.RevisionContentHash, governance.Reference,
            "V146 release another exact selection target");
        var grant = await harness.IssueGrantAsync(Permission.ReleaseRecipe, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe);
        var released = await harness.Runtime.SubmitAsync(command with
        { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.True(released.Disposition == CommandDisposition.Accepted, released.ReasonCode);
        await harness.WaitForVerifiedAsync();

        var page = await harness.ReleaseHistory.QueryAsync(new ReleasedRecipeFilter(PageSize: 20));
        Assert.True(page.Available, page.ReasonCode);
        return Assert.Single(page.Recipes,
            value => value.Record.ReleaseId != harness.Released.Record.ReleaseId);
    }

    /// <summary>Signs in a Technician bundle that does not hold ManageRecipeSelectionMap.</summary>
    internal static async Task CreateTechnicianAsync(RecipeActivationServiceTests.ActivationHarness harness)
    {
        const string userName = "v146.selection.technician";
        const string password = "V146 technician selection password 2026!";
        var principalId = Guid.NewGuid();
        var command = new CreateHumanAccountCommand(Guid.NewGuid(), harness.Invocation(), principalId, userName,
            "V146 Selection Technician", password, HumanRoleBundle.Technician);
        var grant = await harness.IssueGrantAsync(Permission.ManageAccounts, command.CorrelationId,
            principalId.ToString("D"), AuditedCommandKind.CreateHumanAccount);
        var created = await harness.Runtime.SubmitAsync(command with
        { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.True(created.Disposition == CommandDisposition.Accepted, created.ReasonCode);
        await harness.WaitForVerifiedAsync();
        await harness.Sessions.LogoutAsync(harness.Sessions.Current.SessionId);
        var login = await harness.Sessions.SignInAsync(new PasswordSignInRequest(userName, password));
        Assert.True(login.Succeeded, login.ReasonCode);
        await harness.WaitForVerifiedAsync();
    }

    internal static async Task<long> ScalarAsync(ProductionStoreOptions options, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Reads the persisted identity rows: audit ordinal plus canonical payload.</summary>
    internal static async Task<IReadOnlyList<(long Ordinal, byte[] Payload)>> ReadIdentityRowsAsync(
        ProductionStoreOptions options)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT IdentityPosition, Payload FROM audit_entries WHERE Kind='IdentityEvent' ORDER BY Sequence;";
        var rows = new List<(long Ordinal, byte[] Payload)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt64(0), Convert.FromBase64String(reader.GetString(1))));
        return rows;
    }

    internal static async Task<(long Ordinal, byte[] Payload)> SingleIdentityRowAsync(
        ProductionStoreOptions options, IdentityEventKind kind)
    {
        var rows = (await ReadIdentityRowsAsync(options)).Where(value =>
            IdentityAuditEvent.TryReadEventKind(value.Payload, out var actual) && actual == kind).ToArray();
        return Assert.Single(rows);
    }

    /// <summary>
    /// Reads the canonical identity envelope: 4-byte version, marker-prefixed label,
    /// 4-byte field count, then one marker-prefixed value per field.
    /// </summary>
    internal static string?[] ReadIdentityPayloadFields(byte[] payload)
    {
        var offset = 4;
        string? ReadValue()
        {
            var marker = payload[offset++];
            if (marker == 0) return null;
            Assert.Equal(1, marker);
            var length = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
            offset += 4;
            var value = Encoding.UTF8.GetString(payload, offset, length);
            offset += length;
            return value;
        }
        Assert.Equal("IdentityEvent", ReadValue());
        var count = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
        offset += 4;
        var fields = Enumerable.Range(0, count).Select(_ => ReadValue()).ToArray();
        Assert.Equal(payload.Length, offset);
        return fields;
    }
}
