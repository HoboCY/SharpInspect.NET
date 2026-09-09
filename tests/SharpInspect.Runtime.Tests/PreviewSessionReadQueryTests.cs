using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V133_Q01_Schema19IndependentReadersReadFactsWithoutMutation()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);

        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), expectedActive: null,
            "V133 Q01 create schema19 read-query evidence");
        AssertAccepted(await harness.Runtime.SubmitAsync(start));
        await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewReadQueryStartUnavailable");

        var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            cancel: false, "V133 Q01 close schema19 read-query evidence");
        AssertAccepted(await harness.Runtime.SubmitAsync(exit));
        var closed = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed,
            "PreviewReadQueryCloseUnavailable");
        Assert.Equal(PreviewRestorationState.NoActiveBaselineClosed, closed.Restoration);
        await harness.WaitForVerifiedAsync();

        var traceQuery = new SqliteCommandTraceQuery(harness.Options);
        var startFilter = new CommandTraceFilter(CorrelationId: start.CorrelationId,
            PageSize: 20);
        var startFacts = await traceQuery.QueryAsync(startFilter);
        Assert.Contains(startFacts.Records, record =>
            record.CommandKind == AuditedCommandKind.StartPreview &&
            record.Phase == CommandAuditPhase.Outcome);
        Assert.Contains(startFacts.Records, record =>
            record.CommandKind == AuditedCommandKind.StartPreview &&
            record.Phase == CommandAuditPhase.Completed);

        var exitFacts = await traceQuery.QueryAsync(new CommandTraceFilter(
            CorrelationId: exit.CorrelationId, PageSize: 20));
        Assert.Contains(exitFacts.Records, record =>
            record.CommandKind == AuditedCommandKind.Exit &&
            record.Phase == CommandAuditPhase.Outcome);

        var previewHistory = await new SqlitePreviewSessionQuery(harness.Options).QueryAsync(
            new PreviewSessionHistoryFilter(sessionId, PageSize: 128));
        Assert.True(previewHistory.Available, previewHistory.ReasonCode);
        Assert.Contains(previewHistory.Events, value =>
            value.CommandKind == AuditedCommandKind.Exit && value.Terminal &&
            value.Phase == PreviewSessionPhase.Closed &&
            value.Header.StartCorrelationId == start.CorrelationId);

        var draft = await new SqliteRecipeDraftQuery(harness.Options).ReadAsync(
            harness.Source.DraftId, harness.Source.Revision);
        Assert.True(draft.Available, draft.ReasonCode);
        Assert.Equal(harness.Source.RevisionContentHash, draft.Revision!.RevisionContentHash);

        var released = await new SqliteReleasedRecipeQuery(harness.Options).ReadAsync(
            harness.Released.Reference);
        Assert.True(released.Available, released.ReasonCode);
        Assert.Equal(harness.Released.Record.ContentHash, released.Recipe!.Record.ContentHash);

        var contract = await new SqlitePlcResultContractQuery(harness.Options).ReadCurrentAsync();
        Assert.True(contract.Available, contract.ReasonCode);
        Assert.NotNull(contract.Revision);

        var activation = await new SqliteRecipeActivationQuery(harness.Options).ReadCurrentAsync();
        Assert.True(activation.Available, activation.ReasonCode);
        Assert.Null(activation.Record);
        Assert.False(activation.RecoveryRequired);

        // Warm every independent read path before taking the fingerprint. A
        // read-only SQLite connection may materialize its shared-memory file;
        // the database and WAL must remain byte-for-byte stable thereafter.
        var before = await DatabaseFingerprintAsync(harness.Options.DatabasePath);
        var repeatedFacts = await traceQuery.QueryAsync(startFilter);
        var after = await DatabaseFingerprintAsync(harness.Options.DatabasePath);
        Assert.Equal(before, after);
        Assert.Equal(startFacts.ThroughPosition, repeatedFacts.ThroughPosition);
        Assert.Equal(startFacts.Records.Select(value => value.EventId),
            repeatedFacts.Records.Select(value => value.EventId));
    }

    [Fact]
    public async Task V133_Q02_Schema19CommandTraceRequiresPreviewConfiguration()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        await harness.WaitForVerifiedAsync();
        var before = await DatabaseFingerprintAsync(harness.Options.DatabasePath);

        var missingPreview = WithoutPreviewSessions(harness.Options);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(missingPreview)
                .QueryAsync(new CommandTraceFilter(PageSize: 20)).AsTask());

        Assert.Equal("PreviewSessionConfigurationRequired", exception.Message);
        var after = await DatabaseFingerprintAsync(harness.Options.DatabasePath);
        Assert.Equal(before, after);
    }

    private static ProductionStoreOptions WithoutPreviewSessions(ProductionStoreOptions source) =>
        new(source.DatabasePath)
        {
            CommitTimeout = source.CommitTimeout,
            QueryTimeout = source.QueryTimeout,
            QueueCapacity = source.QueueCapacity,
            AuditIntegrityPolicy = source.AuditIntegrityPolicy,
            LocalIdentity = source.LocalIdentity,
            AlarmPolicy = source.AlarmPolicy,
            ExternalAuditAnchor = source.ExternalAuditAnchor,
            AlgorithmResultArchive = source.AlgorithmResultArchive,
            RecipeDrafts = source.RecipeDrafts,
            CameraSetup = source.CameraSetup,
            CameraRecovery = source.CameraRecovery,
            CameraNetwork = source.CameraNetwork,
            ImagingSetup = source.ImagingSetup,
            CalibrationSessions = source.CalibrationSessions,
            CalibrationGovernance = source.CalibrationGovernance,
            RecipeReleases = source.RecipeReleases,
            PlcResultContracts = source.PlcResultContracts,
            RecipeActivations = source.RecipeActivations,
            PreviewSessions = null
        };

    private static async Task<string> DatabaseFingerprintAsync(string databasePath)
    {
        var values = new List<string>();
        foreach (var path in new[] { databasePath, databasePath + "-wal" })
        {
            if (!File.Exists(path))
            {
                values.Add(path + ":Absent");
                continue;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var algorithm = SHA256.Create();
            values.Add(path + ":" +
                Convert.ToHexString(await algorithm.ComputeHashAsync(stream)));
        }

        return string.Join("|", values);
    }
}
