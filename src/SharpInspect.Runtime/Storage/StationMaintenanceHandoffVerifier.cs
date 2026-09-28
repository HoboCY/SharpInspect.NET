using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>The bounded immutable projection of one verified prepared handoff.</summary>
internal sealed record StationMaintenanceHandoffProjection(Guid OperationId, Guid EventId,
    string ConfigurationHash, string OptionsHash, string PolicyHash, string InstallationBindingHash,
    string RuntimeApplicationHash, Guid RuntimeEpoch, int ProcessId, DateTimeOffset ProcessStartedUtc,
    DateTimeOffset ExpiresAtUtc, DateTimeOffset PreparedAtUtc, StationMaintenanceReason Reason,
    Guid CommandEventId, Guid AuthorizationEventId, long PreparedPosition, long PreparedAuditSequence,
    string PreparedAuditHash, string PreparedContentHash, long AdmittedAuditSequence, string AdmittedAuditHash);

internal sealed record StationMaintenanceHandoffVerification(bool Verified, string ReasonCode,
    StationMaintenanceHandoffProjection? Projection);

/// <summary>
/// The shared read-only handoff verifier. It never opens a writer: the database is opened
/// read-only with query_only, the complete audit chain is re-verified, the exact Prepared
/// record is re-proved against its admitted predecessor and its signed central-audit entry,
/// and the artifact signature must match the separately provisioned trusted key. The embedded
/// key is never a trust anchor.
/// </summary>
internal static class StationMaintenanceHandoffVerifier
{
    internal static StationMaintenanceHandoffVerification ValidatePreparedHandoff(
        ProductionStoreOptions options, byte[] artifact, string separatelyProvisionedPublicKeyBase64,
        DateTimeOffset now)
    {
        try
        {
            return Validate(options, artifact, separatelyProvisionedPublicKeyBase64, now);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(false, "StationMaintenanceHandoffInvalid", null);
        }
    }

    private static StationMaintenanceHandoffVerification Validate(ProductionStoreOptions options, byte[] artifact,
        string separatelyProvisionedPublicKeyBase64, DateTimeOffset now)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (options.StationMaintenance is null)
            return new(false, "StationMaintenanceConfigurationRequired", null);
        if (options.AuditIntegrityPolicy is null || options.LocalIdentity is null)
            return new(false, "StationMaintenanceHandoffProfileInvalid", null);
        if (artifact is null || artifact.Length is < 2 or > StationMaintenancePreparedHandoff.MaximumArtifactBytes)
            return new(false, "StationMaintenanceHandoffShapeInvalid", null);
        if (!StationMaintenanceStorageCodec.Utc(now)) return new(false, "StationMaintenanceHandoffTimeInvalid", null);
        var document = DecodeHandoff(artifact);
        if (document is null) return new(false, "StationMaintenanceHandoffShapeInvalid", null);

        var trustedKey = DecodeTrustedKey(separatelyProvisionedPublicKeyBase64);
        if (trustedKey is null) return new(false, "StationMaintenanceHandoffTrustInvalid", null);
        if (!string.Equals(document.PublicKeyBase64, separatelyProvisionedPublicKeyBase64, StringComparison.Ordinal) ||
            !string.Equals(document.KeyId, Convert.ToHexString(SHA256.HashData(trustedKey)), StringComparison.Ordinal))
            return new(false, "StationMaintenanceHandoffTrustMismatch", null);
        if (!VerifySignature(document, trustedKey))
            return new(false, "StationMaintenanceHandoffSignatureInvalid", null);

        if (!DateTimeOffset.TryParseExact(document.PreparedAtUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var preparedAt) || preparedAt.Offset != TimeSpan.Zero ||
            !DateTimeOffset.TryParseExact(document.ExpiresAtUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var expiresAt) || expiresAt.Offset != TimeSpan.Zero ||
            !DateTimeOffset.TryParseExact(document.ProcessStartedUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var processStarted) || processStarted.Offset != TimeSpan.Zero)
            return new(false, "StationMaintenanceHandoffTimeInvalid", null);
        if (now >= expiresAt) return new(false, "StationMaintenanceHandoffExpired", null);
        if (preparedAt > now + TimeSpan.FromMinutes(5)) return new(false, "StationMaintenanceHandoffTimeInvalid", null);
        var lifetime = expiresAt - preparedAt;
        if (lifetime < TimeSpan.FromSeconds(1) || lifetime > TimeSpan.FromMinutes(15) ||
            processStarted >= preparedAt)
            return new(false, "StationMaintenanceHandoffTimeInvalid", null);
        if (!Enum.TryParse<StationMaintenanceReason>(document.Reason, out var reason) || !Enum.IsDefined(reason))
            return new(false, "StationMaintenanceHandoffReasonInvalid", null);

        var option = options.StationMaintenance;
        var expected = SqliteCommandStore.StationMaintenanceConfigurationFor(options);
        if (document.ConfigurationHash != expected.BindingHash || document.OptionsHash != option.BindingHash ||
            document.PolicyHash != expected.PolicyHash ||
            document.InstallationBindingHash != option.InstallationBindingHash ||
            document.RuntimeApplicationHash != option.RuntimeApplicationHash)
            return new(false, "StationMaintenanceHandoffConfigurationMismatch", null);

        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, options);
        var deadline = new StoreDeadline(options.QueryTimeout);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline);
        try
        {
            if (AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) !=
                StationMaintenanceStoreOptions.SchemaVersion)
                return new(false, "StationMaintenanceHandoffSchemaMismatch", null);
            var configuration = SqliteCommandStore.ReadStationMaintenanceConfiguration(database, deadline);
            if (configuration.BindingHash != expected.BindingHash ||
                configuration.OptionsHash != option.BindingHash)
                return new(false, "StationMaintenanceHandoffConfigurationMismatch", null);
            var binding = AuditChainDatabase.Read(database,
                "SELECT StationId,Version,ContentHash,SigningKeyId,PublicKey FROM audit_policy WHERE Id=1 LIMIT 2;",
                deadline, statement => (StationId: SqliteNative.ColumnText(statement, 0),
                    Version: SqliteNative.ColumnText(statement, 1), ContentHash: SqliteNative.ColumnText(statement, 2),
                    KeyId: SqliteNative.ColumnText(statement, 3), PublicKey: SqliteNative.ColumnText(statement, 4)));
            if (binding.Count != 1 || binding[0].StationId != document.StationId ||
                binding[0].ContentHash != options.AuditIntegrityPolicy.ContentHash ||
                binding[0].KeyId is not { Length: 64 } || binding[0].PublicKey is not { Length: > 0 })
                return new(false, "StationMaintenanceHandoffAuditPolicyMismatch", null);
            var auditPolicy = options.AuditIntegrityPolicy;
            var report = AuditChainDatabase.Verify(database, auditPolicy, binding[0].KeyId!, binding[0].PublicKey!,
                new AuditVerificationRequest(0, auditPolicy.MaximumVerificationEntries), startup: false, deadline,
                validateAnchorReceipt: false, storeOptions: options);
            if (report.State != AuditIntegrityState.Verified || report.VerifiedFromSequence != 1 ||
                report.VerifiedThroughSequence != AuditChainDatabase.Tail(database, deadline).Sequence)
                return new(false, "StationMaintenanceHandoffAuditUnverified", null);
            var rows = SqliteCommandStore.ReadStationMaintenanceRows(database, configuration, deadline);
            SqliteCommandStore.RequireStationMaintenanceAuthorityCoverage(database, rows, deadline);
            var prepared = rows.SingleOrDefault(row => row.Position == document.PreparedPosition &&
                row.AuditSequence == document.PreparedAuditSequence && row.AuditHash == document.PreparedAuditHash &&
                row.ContentHash == document.PreparedContentHash && row.Payload.OperationId == document.OperationId &&
                row.Payload.EventId == document.EventId &&
                row.Payload.Phase == StationMaintenanceFactPhase.Prepared);
            if (prepared is null) return new(false, "StationMaintenanceHandoffPreparedMissing", null);
            var admitted = rows.SingleOrDefault(row => row.AuditSequence == document.AdmittedAuditSequence &&
                row.ContentHash == document.AdmittedContentHash &&
                row.Payload.OperationId == document.OperationId &&
                row.Payload.Phase == StationMaintenanceFactPhase.Admitted);
            if (admitted is null || prepared.Payload.Previous is null ||
                prepared.Payload.Previous.Sequence != admitted.AuditSequence ||
                prepared.Payload.Previous.Hash != admitted.AuditHash ||
                prepared.Payload.PreviousContentHash != admitted.ContentHash ||
                prepared.Payload.Command.EventId != document.CommandEventId ||
                prepared.Payload.Command.Sequence != document.CommandSequence ||
                prepared.Payload.Command.Hash != document.CommandHash ||
                prepared.Payload.Authorization.EventId != document.AuthorizationEventId ||
                prepared.Payload.Authorization.Sequence != document.AuthorizationSequence ||
                prepared.Payload.Authorization.Hash != document.AuthorizationHash ||
                prepared.Payload.RuntimeEpoch != document.RuntimeEpoch ||
                prepared.Payload.ProcessId != document.ProcessId ||
                prepared.Payload.ProcessStartedUtc != processStarted ||
                prepared.Payload.ExpiresAtUtc != expiresAt ||
                prepared.Payload.ObservedAtUtc != preparedAt || !string.Equals(prepared.Payload.Reason.ToString(),
                    document.Reason, StringComparison.Ordinal))
                return new(false, "StationMaintenanceHandoffBindingMismatch", null);
            SqliteCommandStore.RequireStationMaintenanceAuthority(database, admitted, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            var projection = new StationMaintenanceHandoffProjection(document.OperationId, document.EventId,
                document.ConfigurationHash, document.OptionsHash, document.PolicyHash,
                document.InstallationBindingHash, document.RuntimeApplicationHash, document.RuntimeEpoch,
                document.ProcessId, processStarted, expiresAt, preparedAt, reason, document.CommandEventId,
                document.AuthorizationEventId, prepared.Position, prepared.AuditSequence, prepared.AuditHash,
                prepared.ContentHash, admitted.AuditSequence, admitted.AuditHash);
            return new(true, "StationMaintenanceHandoffVerified", projection);
        }
        finally
        {
            connection.Dispose();
        }
    }

    private static bool VerifySignature(StationMaintenancePreparedHandoff document, byte[] trustedKey)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(document.SignatureBase64); }
        catch (FormatException) { return false; }
        if (signature.Length != StationMaintenancePreparedHandoff.SignatureBytes ||
            !string.Equals(Convert.ToBase64String(signature), document.SignatureBase64, StringComparison.Ordinal))
            return false;
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(trustedKey, out var read);
            if (read != trustedKey.Length || verifier.KeySize != 256) return false;
            return verifier.VerifyData(document.EncodeUnsigned(), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static byte[]? DecodeTrustedKey(string? publicKeyBase64)
    {
        if (publicKeyBase64 is not { Length: > 0 and <= 1024 }) return null;
        try
        {
            var bytes = Convert.FromBase64String(publicKeyBase64);
            if (bytes.Length == 0 || !string.Equals(Convert.ToBase64String(bytes), publicKeyBase64,
                    StringComparison.Ordinal))
                return null;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            return read == bytes.Length && key.KeySize == 256 ? bytes : null;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException)
        { return null; }
    }

    /// <summary>
    /// Re-decodes the canonical artifact and rejects every non-canonical, non-UTF8, wrong-shape
    /// or out-of-bounds field before any field is trusted.
    /// </summary>
    private static StationMaintenancePreparedHandoff? DecodeHandoff(byte[] artifact)
    {
        try
        {
            using var input = new MemoryStream(artifact, writable: false);
            using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
            int ReadInteger()
            {
                var bytes = reader.ReadBytes(4);
                if (bytes.Length != 4) throw new InvalidOperationException("StationMaintenanceHandoffShapeInvalid");
                return BinaryPrimitives.ReadInt32BigEndian(bytes);
            }
            string? ReadValue(int maximumBytes)
            {
                var marker = reader.ReadByte();
                if (marker == 0) return null;
                if (marker != 1) throw new InvalidOperationException("StationMaintenanceHandoffShapeInvalid");
                var length = ReadInteger();
                if (length < 0 || length > maximumBytes)
                    throw new InvalidOperationException("StationMaintenanceHandoffShapeInvalid");
                var bytes = reader.ReadBytes(length);
                if (bytes.Length != length) throw new InvalidOperationException("StationMaintenanceHandoffShapeInvalid");
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            if (ReadInteger() != AuditCanonical.CanonicalizationVersion) return null;
            var kind = ReadValue(64);
            var count = ReadInteger();
            if (kind != StationMaintenancePreparedHandoff.EncodeLabel || count != 31) return null;
            var fields = Enumerable.Range(0, count).Select(index => ReadValue(512)).ToArray();
            if (input.Position != input.Length || fields.Any(field => field is null)) return null;
            var document = new StationMaintenancePreparedHandoff
            {
                FormatVersion = fields[0]!, OperationId = Guid.Parse(fields[1]!), EventId = Guid.Parse(fields[2]!),
                ConfigurationHash = fields[3]!, OptionsHash = fields[4]!, PolicyHash = fields[5]!,
                InstallationBindingHash = fields[6]!, RuntimeApplicationHash = fields[7]!, StationId = fields[8]!,
                KeyId = fields[9]!, PublicKeyBase64 = fields[10]!,
                PreparedPosition = long.Parse(fields[11]!, NumberStyles.None, CultureInfo.InvariantCulture),
                PreparedContentHash = fields[12]!,
                PreparedAuditSequence = long.Parse(fields[13]!, NumberStyles.None, CultureInfo.InvariantCulture),
                PreparedAuditHash = fields[14]!,
                AdmittedAuditSequence = long.Parse(fields[15]!, NumberStyles.None, CultureInfo.InvariantCulture),
                AdmittedAuditHash = fields[16]!, AdmittedContentHash = fields[17]!,
                CommandEventId = Guid.Parse(fields[18]!),
                CommandSequence = long.Parse(fields[19]!, NumberStyles.None, CultureInfo.InvariantCulture),
                CommandHash = fields[20]!, AuthorizationEventId = Guid.Parse(fields[21]!),
                AuthorizationSequence = long.Parse(fields[22]!, NumberStyles.None, CultureInfo.InvariantCulture),
                AuthorizationHash = fields[23]!, RuntimeEpoch = Guid.Parse(fields[24]!),
                ProcessId = int.Parse(fields[25]!, NumberStyles.None, CultureInfo.InvariantCulture),
                ProcessStartedUtc = fields[26]!, Reason = fields[27]!, ExpiresAtUtc = fields[28]!,
                PreparedAtUtc = fields[29]!, SignatureBase64 = fields[30]!
            };
            if (!artifact.AsSpan().SequenceEqual(StationMaintenancePreparedHandoff.Encode(document))) return null;
            return IsWellFormed(document) ? document : null;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException
            or DecoderFallbackException or EndOfStreamException or ArgumentException)
        { return null; }
    }

    private static bool IsWellFormed(StationMaintenancePreparedHandoff value)
    {
        static bool Hash(string? text) => StationMaintenanceStorageCodec.Hash(text);
        if (value.FormatVersion != StationMaintenancePreparedHandoff.Version.ToString(CultureInfo.InvariantCulture) ||
            value.OperationId == Guid.Empty || value.EventId == Guid.Empty || value.RuntimeEpoch == Guid.Empty ||
            value.CommandEventId == Guid.Empty || value.AuthorizationEventId == Guid.Empty || value.ProcessId <= 0 ||
            !Hash(value.ConfigurationHash) || !Hash(value.OptionsHash) || !Hash(value.PolicyHash) ||
            !Hash(value.InstallationBindingHash) || !Hash(value.RuntimeApplicationHash) ||
            value.StationId is not { Length: > 0 and <= 128 } || value.KeyId is not { Length: 64 } ||
            value.KeyId.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')) ||
            value.PublicKeyBase64 is not { Length: > 0 and <= 1024 } ||
            !Hash(value.PreparedContentHash) || value.PreparedPosition <= 0 ||
            value.PreparedAuditSequence <= 1 || !Hash(value.PreparedAuditHash) ||
            value.AdmittedAuditSequence <= 1 || !Hash(value.AdmittedAuditHash) || !Hash(value.AdmittedContentHash) ||
            value.CommandSequence <= 1 || !Hash(value.CommandHash) || value.AuthorizationSequence <= 1 ||
            !Hash(value.AuthorizationHash) || value.SignatureBase64 is not { Length: > 0 and <= 256 })
            return false;
        return true;
    }
}
