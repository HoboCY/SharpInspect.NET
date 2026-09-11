using System.Collections.ObjectModel;
using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;
using AdmissionEventKind = SharpInspect.Abstractions.ProductionAdmissionEventKind;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore : IProductionAdmissionTerminalWriter
{
    internal event Action? ProductionAdmissionMaterialChanging;

    private void RegisterProductionAdmissionCommitFence(sqlite3 database)
    {
        var deadline = new StoreDeadline(_options.QueryTimeout);
        var previous = ReadProductionAdmissionDurableHeads(database, ReadIdentityState(database, deadline), deadline);
        ProductionAdmissionCommitFence.Register(database, commitDeadline =>
        {
            // Inspect the actual transaction, so every selected ledger and identity
            // authority mutation is covered without classifying request types.
            var current = ReadProductionAdmissionDurableHeads(database,
                ReadIdentityState(database, commitDeadline), commitDeadline);
            if (!ProductionAdmissionHeadsEqual(previous, current))
            {
                ProductionAdmissionMaterialChanging?.Invoke();
                // Move the baseline only after COMMIT succeeds. Otherwise a retry
                // of the same H2 after rollback could miss the next invalidation.
                return () => previous = current;
            }
            return null;
        });
    }

    internal static bool ProductionAdmissionHeadsEqual(IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> current) => expected.Count == current.Count &&
        expected.All(pair => current.TryGetValue(pair.Key, out var value) && value == pair.Value);

    internal async ValueTask<IReadOnlyDictionary<string, string>> ReadProductionAdmissionDurableHeadsAsync(
        CancellationToken cancellationToken)
    {
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed || !ProductionAdmissionEnabled || _signingKey is null)
            throw new InvalidOperationException("ProductionAdmissionStoreUnavailable");
        return await Task.Run<IReadOnlyDictionary<string, string>>(() =>
        {
            using var connection = SqliteNative.Open(_databasePath!, true);
            var database = connection.Handle!;
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.ConfigureSqliteLimit(database, _options);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
            try
            {
                VerifyProductionAdmissionTransaction(database, deadline);
                var heads = ReadProductionAdmissionDurableHeads(database, ReadIdentityState(database, deadline), deadline);
                SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                return heads;
            }
            catch { Rollback(database); throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string> ReadProductionAdmissionDurableHeads(sqlite3 database,
        IdentityAuthorityState state, StoreDeadline deadline)
    {
        var identity = new List<string?> { state.StationId, state.InstallationKeyId, state.PolicyContentHash,
            state.RecoveryKitId?.ToString("D"), state.RecoveryKitVersion.ToString(CultureInfo.InvariantCulture),
            state.KitState.ToString(), state.RecoveredPrincipalId?.ToString("D"),
            state.RecoveryOwnerPrincipalId?.ToString("D"), state.RecoveryOperationRootHash };
        foreach (var account in state.EnumerateAccounts().OrderBy(account => account.PrincipalId))
        {
            identity.AddRange(new[] { account.PrincipalId.ToString("D"), account.CredentialId.ToString("D"),
                account.CredentialRevision.ToString(CultureInfo.InvariantCulture),
                account.AuthorizationRevision.ToString(CultureInfo.InvariantCulture), account.Enabled.ToString(),
                string.Join(",", account.Permissions.OrderBy(permission => permission)) });
        }
        foreach (var code in state.RecoveryCodes.OrderBy(code => code.CodeId))
            identity.AddRange(new[] { code.CodeId.ToString("D"), code.Consumed.ToString(), code.Revoked.ToString() });
        var heads = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["identity-authority"] = ProductionAdmissionCanonical.Hash("production-identity-authority-v1", identity.ToArray()),
            ["identity-policy"] = state.PolicyContentHash
        };
        // These are durable governed facts. Ordinary command/identity audit traffic,
        // heartbeat time and owned cycle/transport progress do not change their heads.
        // Their live health, generation, recovery and resource gates are observed
        // separately; their immutable store activation/configuration entries remain material.
        var selected = AuditChainDatabase.Read(database, @"
            SELECT Kind,Hash FROM audit_entries WHERE Sequence IN (
                SELECT MAX(Sequence) FROM audit_entries
                WHERE Kind NOT IN ('CommandFact','IdentityEvent','ProductionAdmissionEvent',
                    'ProductionInspectionEvent','PlcCommunicationEvent','ProductionArmEvent')
                GROUP BY Kind) ORDER BY Kind LIMIT 63;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0)!, Hash: SqliteNative.ColumnText(statement, 1)!));
        AuditChainDatabase.Require(selected.Count <= 62, "ProductionAdmissionDurableHeadsCapacityExceeded");
        foreach (var row in selected) heads.Add("ledger." + row.Kind, row.Hash);
        return new ReadOnlyDictionary<string, string>(heads);
    }

    internal static string ProductionAdmissionAuthorizationTarget(string stationId,
        ProductionAdmissionReport report, Guid correlationId, Guid attemptId,
        IReadOnlyDictionary<string, string> expectedHeads) => ProductionAdmissionCanonical.Hash(
            "production-admission-authorization-target-v1", stationId, report.ContentHash,
            correlationId.ToString("D"), attemptId.ToString("D"), EncodeHeads(expectedHeads));

    private ProductionAdmissionWriteContext? PrepareProductionAdmissionWrite(sqlite3 database,
        IdentityAuthorityState state, IdentityUpdate update, StoreDeadline deadline)
    {
        if (update.Result is not ProductionAdmissionTransactionResult &&
            update.Result is not ProductionAdmissionTerminalMutation) return null;
        var terminal = update.Result as ProductionAdmissionTerminalMutation;
        var initial = update.Result as ProductionAdmissionTransactionResult;
        AuditChainDatabase.Require(ProductionAdmissionEnabled && update.Events.Count == 1 &&
            update.CommandFacts is { Count: 1 }, "ProductionAdmissionTransactionInvalid");
        var authorization = update.Events[0];
        var command = update.CommandFacts![0];
        // Anonymous/invalid-session rejections still have identity and command audit;
        // they cannot be represented as authenticated-human report ledger entries.
        if (authorization.ActorPrincipalId is null || authorization.SessionId is null)
        {
            AuditChainDatabase.Require(initial is { Accepted: false }, "ProductionAdmissionActorRequired");
            return null;
        }
        var currentHeads = ReadProductionAdmissionDurableHeads(database, state, deadline);
        if (initial is { Accepted: true })
        {
            AuditChainDatabase.Require(initial.Report.CanArm && initial.RuntimeEpoch == initial.Report.RuntimeEpoch &&
                initial.AdmissionGeneration == initial.Report.AdmissionGeneration &&
                initial.ExpectedDurableHeads.Count == currentHeads.Count && initial.ExpectedDurableHeads.All(pair =>
                    currentHeads.TryGetValue(pair.Key, out var value) && value == pair.Value),
                "ProductionAdmissionDurableHeadsChanged");
        }
        var report = initial?.Report ?? terminal!.Admission.Report;
        var expectedHeads = initial?.ExpectedDurableHeads ?? terminal!.Admission.ExpectedDurableHeads;
        var kind = initial is not null ? initial.Accepted ? AdmissionEventKind.Admitted : AdmissionEventKind.Rejected
            : terminal!.Succeeded ? AdmissionEventKind.Completed : AdmissionEventKind.Failed;
        var reserve = AuditChainDatabase.EnsureProductionAdmissionTransactionCapacity(database, _policy!,
            update.Events.Count + update.CommandFacts.Count + 1, initial?.Accepted == true, deadline,
            completingAdmitted: terminal is not null);
        var policy = _options.LocalIdentity!.AuthorizationPolicy;
        var history = new ProductionAdmissionHistoryEvent(1, kind, report, command.CorrelationId, command.AttemptId,
            command.RuntimeEpoch, report.AdmissionGeneration, authorization.ActorPrincipalId!.Value,
            authorization.SessionId!.Value, authorization.AuthorizationRevision,
            new RecipeContractReference(policy.Id, policy.Version, policy.ContentHash), authorization.StepUpGrantId,
            expectedHeads, currentHeads, ProductionAdmissionAuthorizationTarget(state.StationId, report,
                command.CorrelationId, command.AttemptId, expectedHeads), command.ReasonCode,
            null, null, null, null, 0, null, null);
        return new(history, reserve);
    }

    private void AppendProductionAdmissionWrite(sqlite3 database, ProductionAdmissionWriteContext context,
        long identitySequence, CommandAuditFact command, StoreDeadline deadline)
    {
        var commandReference = AuditChainDatabase.Read(database, @"
            SELECT e.Sequence,e.Hash FROM audit_entries e JOIN command_facts f ON f.Position=e.FactPosition
            WHERE e.Kind='CommandFact' AND f.EventId=? LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0), Hash: SqliteNative.ColumnText(statement, 1)),
            command.EventId.ToString("D")).Single();
        var identityHash = AuditChainDatabase.Text(database,
            "SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind='IdentityEvent';", deadline,
            identitySequence.ToString(CultureInfo.InvariantCulture));
        var value = context.Event;
        AppendProductionAdmissionEvent(database, new ProductionAdmissionHistoryEvent(1, value.Kind, value.Report,
            value.CorrelationId, value.AttemptId, value.RuntimeEpoch, value.AdmissionGeneration,
            value.ActorPrincipalId, value.ActorSessionId, value.ActorAuthorizationRevision, value.AuthorizationPolicy,
            value.StepUpGrantId, value.ExpectedDurableHeads, value.CurrentDurableHeads, value.AuthorizationTarget,
            value.ReasonCode, commandReference.Sequence, commandReference.Hash, identitySequence, identityHash,
            0, null, null), deadline, context.TakeReserve());
    }

    public ValueTask<IdentityWriteResult> CompleteProductionAdmissionAsync(Guid correlationId, Guid attemptId,
        Guid runtimeEpoch, long admissionGeneration, string reasonCode, StoreDeadline deadline) =>
        UpdateIdentityCommandAsync(correlationId, (_, _) => new IdentityUpdate(
            new ProductionAdmissionTerminalRequest(correlationId, attemptId, runtimeEpoch, admissionGeneration,
                reasonCode), Array.Empty<IdentityAuditEvent>()), CancellationToken.None, deadline);

    private IdentityUpdate BuildProductionAdmissionTerminal(sqlite3 database, IdentityAuthorityState state,
        ProductionAdmissionTerminalRequest request, StoreDeadline deadline)
    {
        var rows = ReadProductionAdmissionRows(database, _options.ProductionAdmission!, deadline);
        var admitted = rows.Select(row => row.Event).LastOrDefault(value => value.AttemptId == request.AttemptId);
        AuditChainDatabase.Require(admitted is { Kind: AdmissionEventKind.Admitted } &&
            admitted.CorrelationId == request.CorrelationId && admitted.RuntimeEpoch == request.RuntimeEpoch &&
            admitted.AdmissionGeneration == request.Generation, "ProductionAdmissionTerminalContextMismatch");
        var original = ReadFact(database, request.AttemptId, 1, deadline);
        AuditChainDatabase.Require(original is not null && original.CommandKind == AuditedCommandKind.ArmProduction,
            "ProductionAdmissionCommandAuditMismatch");
        var reason = request.Reason;
        if (reason == "ProductionAdmissionFinalized" && !ProductionAdmissionHeadsEqual(
            admitted!.ExpectedDurableHeads, ReadProductionAdmissionDurableHeads(database, state, deadline)))
            reason = "ProductionAdmissionChanged";
        var succeeded = reason == "ProductionAdmissionFinalized";
        var now = DateTimeOffset.UtcNow;
        if (now < state.LastObservedUtc) now = state.LastObservedUtc;
        var command = original! with { EventId = Guid.NewGuid(), OccurredAtUtc = now,
            Phase = succeeded ? CommandAuditPhase.Completed : CommandAuditPhase.Failed,
            Disposition = null, ReasonCode = reason };
        var identity = new IdentityAuditEvent(command.EventId,
            succeeded ? IdentityEventKind.ProductionAdmissionCompleted : IdentityEventKind.ProductionAdmissionFailed,
            now, state.StationId, admitted!.ActorPrincipalId, null, null, null, reason,
            SessionId: admitted.ActorSessionId, ActorPrincipalId: admitted.ActorPrincipalId,
            CommandCorrelationId: request.CorrelationId, StepUpGrantId: admitted.StepUpGrantId,
            RequiredPermission: Permission.ArmProduction.ToString(),
            AuthorizationRevision: admitted.ActorAuthorizationRevision, ActionTargetId: state.StationId,
            BoundCommandCorrelationId: request.CorrelationId, ActionCommandKind: AuditedCommandKind.ArmProduction.ToString(),
            OperationId: request.CorrelationId);
        return new IdentityUpdate(new ProductionAdmissionTerminalMutation(admitted, succeeded), new[] { identity },
            new[] { command });
    }

    private sealed record ProductionAdmissionTerminalRequest(Guid CorrelationId, Guid AttemptId,
        Guid RuntimeEpoch, long Generation, string Reason);
    private sealed record ProductionAdmissionTerminalMutation(ProductionAdmissionHistoryEvent Admission, bool Succeeded);
    private sealed class ProductionAdmissionWriteContext
    {
        internal ProductionAdmissionWriteContext(ProductionAdmissionHistoryEvent value, long reserve)
        { Event = value; _reserve = reserve; }
        internal ProductionAdmissionHistoryEvent Event { get; }
        private long _reserve;
        internal long TakeReserve() => _reserve--;
    }
    private void VerifyProductionAdmissionTransaction(sqlite3 database, StoreDeadline deadline)
    {
        var key = _signingKey!;
        var admissionOptions = _options.ProductionAdmission!;
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(admissionOptions);
        var policy = _options.AuditIntegrityPolicy ??
            throw new InvalidOperationException("AuditPolicyNotConfigured");
        var verification = AuditChainDatabase.Verify(database, policy,
            key.KeyId, key.PublicKeyBase64,
            new AuditVerificationRequest(0, policy.MaximumVerificationEntries), startup: false,
            deadline, validateAnchorReceipt: false,
            archiveOptions: _options.AlgorithmResultArchive,
            recipeDraftOptions: _options.RecipeDrafts,
            cameraSetupOptions: _options.CameraSetup,
            cameraRecoveryOptions: _options.CameraRecovery,
            cameraNetworkOptions: _options.CameraNetwork,
            imagingSetupOptions: _options.ImagingSetup,
            calibrationSessionOptions: _options.CalibrationSessions,
            governanceOptions: _options.CalibrationGovernance,
            releaseOptions: _options.RecipeReleases,
            contractOptions: _options.PlcResultContracts,
            activationOptions: _options.RecipeActivations,
            previewOptions: _options.PreviewSessions,
            importOptions: _options.CalibrationImports,
            manualOptions: _options.ManualInspections,
            productionAdmissionOptions: admissionOptions,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: _options.ProductionInspections,
                productionRecoveryOptions: _options.ProductionRecovery,
                partIdentityOptions: _options.PartIdentities,
                productionArmOptions: _options.ProductionArming, recipeSelectionOptions: _options.RecipeSelections, recipeLifecycleOptions: _options.RecipeLifecycle);
            RecipeTransferReadGuard.RequireVerified(database, verification, deadline, _options);
        if (_options.PlcCommunication is not null)
            AuditChainDatabase.RequireFullPlcCommunicationVerification(database, verification, deadline,
                _options.PlcCommunication);
            TraceStoragePolicyReadGuard.RequireVerified(database, verification, deadline, _options);
                RequireStationQualificationWriteSnapshot(database, verification, deadline);
        if (_options.AlarmPolicy is not null)
            AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
        if (_options.AlgorithmResultArchive is not null)
            AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
        if (_options.RecipeDrafts is not null)
            AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                _options.RecipeDrafts);
        if (_options.CameraSetup is not null)
            AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                _options.CameraSetup);
        if (_options.CameraRecovery is not null)
            AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification,
                deadline, _options.CameraRecovery);
        if (_options.CameraNetwork is not null)
            AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification,
                deadline, _options.CameraNetwork);
        if (_options.ImagingSetup is not null)
            AuditChainDatabase.RequireFullImagingSetupVerification(database, verification,
                deadline, _options.ImagingSetup);
        if (_options.CalibrationGovernance is not null)
            AuditChainDatabase.RequireFullCalibrationGovernanceVerification(database, verification,
                deadline, _options.CalibrationGovernance);
        if (_options.RecipeReleases is not null)
            AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification,
                deadline, _options.RecipeReleases, _options.RecipeDrafts,
                _options.CalibrationGovernance);
        if (_options.PlcResultContracts is not null)
            AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification,
                deadline, _options.PlcResultContracts);
        if (_options.RecipeActivations is not null)
            AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification,
                deadline, _options.RecipeActivations, _options.RecipeReleases,
                _options.PlcResultContracts, _options.CalibrationGovernance, recipeLifecycleOptions: _options.RecipeLifecycle);
        if (_options.PreviewSessions is not null)
            AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification,
                deadline, _options.PreviewSessions);
        if (_options.CalibrationImports is not null)
            AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification,
                deadline, _options.CalibrationImports);
        if (_options.ManualInspections is not null)
            AuditChainDatabase.RequireFullManualInspectionVerification(database, verification,
                deadline, _options.ManualInspections);
        AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification,
            deadline, admissionOptions);
    }

}
