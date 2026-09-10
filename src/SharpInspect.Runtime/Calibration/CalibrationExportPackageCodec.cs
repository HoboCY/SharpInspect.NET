using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>
/// Strict codec for untrusted calibration export packages. The container is a
/// closed binary format; it has no paths, archives, executable payloads, or code
/// loading hooks.
/// </summary>
internal static class CalibrationExportPackageCodec
{
    internal const int FormatVersion = 1;
    internal const int MaximumBytes = CalibrationExportPackage.MaximumBytes;

    private const int Magic = 0x31584353; // SCX1, little-endian on the wire.
    private const int ManifestMagic = 0x314D4353; // SCM1.
    private const byte ContainerVersion = 1;
    private const byte ProfileFlag = 1;
    private const int HeaderBytes = 4 + 1 + 1 + 2 + 4 + 4 + 32;
    private const int MemberHeaderBytes = 1 + 3 + 4 + 32;
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumTextBytes = 4096;
    private const string ZeroHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    internal static CalibrationExportPackage Encode(string sourceStationId,
        CalibrationSessionEvidence evidence, CalibrationAcceptancePolicy policy,
        IReadOnlyList<CalibrationFrameImage> images,
        PublishedCalibrationProfileVersion? profile = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count is < 1 or > 64)
            throw new ArgumentException("CalibrationExportFrameSetInvalid", nameof(images));

        CalibrationSessionStorageCodec.ValidateTransferEvidenceForExport(evidence);
        var source = CreateSourceIdentity(evidence, profile);
        var expectedProcedure = evidence.Header.Command.Plan.Procedure.Procedure;
        if (expectedProcedure != policy.ProcedureContract)
            throw new ArgumentException("CalibrationExportProcedurePolicyMismatch", nameof(policy));
        if (evidence.Header.Command.Plan.Requirement.AcceptancePolicy != policy.Reference)
            throw new ArgumentException("CalibrationExportPolicyRequirementMismatch", nameof(policy));

        var copiedImages = ValidateAndOrderImages(evidence, images);
        var evidenceBytes = CalibrationSessionStorageCodec.EncodeTransferEvidence(evidence);
        var policyBytes = CalibrationSessionStorageCodec.EncodeTransferPolicy(policy);
        var frameBytes = EncodeFrameImages(copiedImages);
        var profileBytes = profile is null ? null : CalibrationGovernanceCodec.Encode(profile);
        if (profileBytes is not null && profileBytes.Length > CalibrationGovernanceCodec.MaximumPayloadBytes)
            throw new ArgumentException("CalibrationExportProfilePayloadOversized", nameof(profile));

        var members = new List<MemberPayload>(4)
        {
            new(CalibrationExportMemberKind.SessionEvidence, evidenceBytes),
            new(CalibrationExportMemberKind.AcceptancePolicy, policyBytes),
            new(CalibrationExportMemberKind.FrameImages, frameBytes)
        };
        if (profileBytes is not null)
            members.Add(new(CalibrationExportMemberKind.PublishedProfile, profileBytes));

        var memberManifests = members.Select(member =>
            new CalibrationExportMemberManifest(member.Kind, member.Payload.Length,
                Hash(member.Payload))).ToArray();
        var packageId = Guid.NewGuid();
        var temporary = evidence.TemporaryConfiguration ??
            throw new ArgumentException("CalibrationExportTemporaryConfigurationMissing", nameof(evidence));
        var requestedGeometry = CalibrationFrameGeometry.FromRequested(temporary.Requested);
        var effectiveGeometry = CalibrationFrameGeometry.FromEffective(temporary.Effective);
        var manifest = new CalibrationExportManifest(
            CalibrationExportManifest.CurrentFormatVersion, packageId, sourceStationId, source,
            expectedProcedure, policy.Reference, evidence.Header.Binding.Target,
            evidence.Header.Command.ExpectedImagingSetup, requestedGeometry, effectiveGeometry,
            memberManifests, ZeroHash);
        var manifestContentHash = Hash(EncodeManifest(manifest));
        manifest = RebuildManifest(manifest, manifestContentHash);
        var manifestBytes = EncodeManifest(manifest);
        if (manifestBytes.Length > MaximumManifestBytes)
            throw new ArgumentException("CalibrationExportManifestOversized", nameof(evidence));

        var manifestHash = SHA256.HashData(manifestBytes);
        var totalLength = checked(HeaderBytes + manifestBytes.Length +
            members.Sum(member => checked(MemberHeaderBytes + member.Payload.Length)));
        if (totalLength > MaximumBytes)
            throw new ArgumentException("CalibrationExportPackageSizeInvalid", nameof(images));

        var writer = new ExportWriter(MaximumBytes);
        writer.Int32(Magic);
        writer.Byte(ContainerVersion);
        writer.Byte(profile is null ? (byte)0 : ProfileFlag);
        writer.UInt16((ushort)members.Count);
        writer.Int32(totalLength);
        writer.Int32(manifestBytes.Length);
        writer.Bytes(manifestHash);
        writer.Bytes(manifestBytes);
        foreach (var member in members)
        {
            writer.Byte((byte)member.Kind);
            writer.Byte(0);
            writer.Byte(0);
            writer.Byte(0);
            writer.Int32(member.Payload.Length);
            writer.Bytes(SHA256.HashData(member.Payload));
            writer.Bytes(member.Payload);
        }

        var bytes = writer.ToArray();
        if (bytes.Length != totalLength)
            throw new InvalidOperationException("CalibrationExportContainerLengthMismatch");
        return new CalibrationExportPackage(bytes);
    }

    internal static CalibrationExportPackageContents Decode(CalibrationExportPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var bytes = package.GetBytes();
        try
        {
            if (bytes.Length is < HeaderBytes or > MaximumBytes)
                throw Invalid("CalibrationExportPackageSizeInvalid");
            var reader = new ExportReader(bytes);
            if (reader.Int32() != Magic || reader.Byte() != ContainerVersion)
                throw Invalid("CalibrationExportContainerVersionUnsupported");
            var flags = reader.Byte();
            if ((flags & ~ProfileFlag) != 0)
                throw Invalid("CalibrationExportContainerFlagsInvalid");
            var memberCount = reader.UInt16();
            if (memberCount is < 3 or > 4)
                throw Invalid("CalibrationExportMemberCountInvalid");
            var totalLength = reader.Int32();
            if (totalLength != bytes.Length)
                throw Invalid("CalibrationExportContainerLengthMismatch");
            var manifestLength = reader.Int32();
            if (manifestLength is < 1 or > MaximumManifestBytes)
                throw Invalid("CalibrationExportManifestSizeInvalid");
            var manifestHash = reader.Bytes(32);
            var manifestBytes = reader.Bytes(manifestLength);
            if (!CryptographicOperations.FixedTimeEquals(manifestHash,
                    SHA256.HashData(manifestBytes)))
                throw Invalid("CalibrationExportManifestHashMismatch");

            var manifest = DecodeManifest(manifestBytes);
            if (manifest.Members.Count != memberCount ||
                ((flags & ProfileFlag) != 0) !=
                manifest.Members.Any(member => member.Kind == CalibrationExportMemberKind.PublishedProfile))
                throw Invalid("CalibrationExportMemberManifestMismatch");

            var payloads = new Dictionary<CalibrationExportMemberKind, byte[]>();
            CalibrationExportMemberKind? previousKind = null;
            for (var index = 0; index < memberCount; index++)
            {
                var kind = reader.ByteEnum<CalibrationExportMemberKind>();
                if (previousKind is { } previous && kind <= previous)
                    throw Invalid("CalibrationExportMemberOrderInvalid");
                previousKind = kind;
                if (reader.Byte() != 0 || reader.Byte() != 0 || reader.Byte() != 0)
                    throw Invalid("CalibrationExportMemberReservedBytesInvalid");
                var length = reader.Int32();
                if (length is < 1 or > MaximumBytes)
                    throw Invalid("CalibrationExportMemberSizeInvalid");
                var memberHash = reader.Bytes(32);
                var payload = reader.Bytes(length);
                if (!payloads.TryAdd(kind, payload))
                    throw Invalid("CalibrationExportMemberDuplicate");
                if (!CryptographicOperations.FixedTimeEquals(memberHash,
                        SHA256.HashData(payload)))
                    throw Invalid("CalibrationExportMemberHashMismatch");
                var descriptor = manifest.Members.SingleOrDefault(member => member.Kind == kind);
                if (descriptor is null || descriptor.Length != length ||
                    !string.Equals(descriptor.ContentHash, Convert.ToHexString(memberHash),
                        StringComparison.Ordinal))
                    throw Invalid("CalibrationExportMemberManifestMismatch");
            }
            reader.EnsureEnd();

            var evidence = CalibrationSessionStorageCodec.DecodeTransferEvidence(
                payloads[CalibrationExportMemberKind.SessionEvidence]);
            var policy = CalibrationSessionStorageCodec.DecodeTransferPolicy(
                payloads[CalibrationExportMemberKind.AcceptancePolicy]);
            var images = DecodeFrameImages(payloads[CalibrationExportMemberKind.FrameImages]);
            PublishedCalibrationProfileVersion? profile = null;
            if (payloads.TryGetValue(CalibrationExportMemberKind.PublishedProfile,
                    out var profilePayload))
            {
                profile = CalibrationGovernanceCodec.Decode(
                    CalibrationGovernanceCodec.CalibrationProfilePublished, profilePayload)
                    as PublishedCalibrationProfileVersion
                    ?? throw Invalid("CalibrationExportProfilePayloadInvalid");
            }

            ValidateDecodedContents(manifest, evidence, policy, images, profile);
            return new CalibrationExportPackageContents(manifest, evidence, policy, profile, images);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
                                           not StackOverflowException and
                                           not ArgumentException)
        {
            throw new ArgumentException("CalibrationExportPackageInvalid", nameof(package), exception);
        }
    }

    private static CalibrationExportSourceIdentity CreateSourceIdentity(
        CalibrationSessionEvidence evidence, PublishedCalibrationProfileVersion? profile)
    {
        var temporary = evidence.TemporaryConfiguration ??
            throw new ArgumentException("CalibrationExportTemporaryConfigurationMissing", nameof(evidence));
        CalibrationCandidateReference? candidate = null;
        if (evidence.Candidate is { } candidateEvidence)
        {
            candidate = new CalibrationCandidateReference(candidateEvidence.SessionId,
                candidateEvidence.CandidateId, candidateEvidence.ContentHash);
        }
        CalibrationProfileReference? profileReference = profile?.Reference;
        if (profile is not null)
        {
            var publishedCandidateEvidence = evidence.Candidate;
            if (candidate is null || publishedCandidateEvidence is null)
                throw new ArgumentException("CalibrationExportProfileSourceMismatch", nameof(profile));
            if (profile.SourceCandidate != candidate ||
                profile.Content.SourceEvidenceHash != candidate.CandidateContentHash ||
                profile.Content.Coefficients.ContentHash !=
                    publishedCandidateEvidence.Result.Coefficients.ContentHash ||
                profile.Content.Requirement.ContentHash !=
                    evidence.Header.Command.Plan.Requirement.ContentHash ||
                profile.Content.Procedure != evidence.Header.Command.Plan.Procedure.Procedure ||
                profile.AcceptancePolicy !=
                    evidence.Header.Command.Plan.Requirement.AcceptancePolicy ||
                profile.Content.Device.ContentHash != evidence.Header.Binding.Target.ContentHash ||
                profile.Content.ImagingSetup != evidence.Header.Command.ExpectedImagingSetup ||
                profile.Content.RequestedGeometry.ContentHash !=
                    CalibrationFrameGeometry.FromRequested(temporary.Requested).ContentHash ||
                profile.Content.EffectiveGeometry.ContentHash !=
                    CalibrationFrameGeometry.FromEffective(temporary.Effective).ContentHash)
                throw new ArgumentException("CalibrationExportProfileSourceMismatch", nameof(profile));
        }
        return new CalibrationExportSourceIdentity(evidence.Header.SessionId, candidate,
            profileReference);
    }

    private static IReadOnlyList<CalibrationFrameImage> ValidateAndOrderImages(
        CalibrationSessionEvidence evidence, IReadOnlyList<CalibrationFrameImage> images)
    {
        var expected = evidence.Frames.ToDictionary(frame => frame.FrameId);
        var seen = new HashSet<Guid>();
        foreach (var image in images)
        {
            if (image is null || !seen.Add(image.Frame.FrameId) ||
                !expected.TryGetValue(image.Frame.FrameId, out var frame) ||
                image.Frame.SessionId != evidence.Header.SessionId ||
                image.Frame.SourceHash != frame.SourceHash || image.Frame.PixelHash != frame.PixelHash ||
                image.Frame.ByteLength != frame.ByteLength)
                throw new ArgumentException("CalibrationExportFrameEvidenceMismatch", nameof(images));
        }
        if (seen.Count != expected.Count)
            throw new ArgumentException("CalibrationExportFrameEvidenceMissing", nameof(images));
        return images.OrderBy(image => image.Frame.FrameId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
    }

    private static byte[] EncodeFrameImages(IReadOnlyList<CalibrationFrameImage> images)
    {
        var writer = new ExportWriter(MaximumBytes);
        writer.Int32(images.Count);
        foreach (var image in images)
        {
            var manifest = CalibrationSessionStorageCodec.EncodeFrameManifest(image.Frame);
            if (image.Frame.ByteLength > int.MaxValue ||
                writer.Length > MaximumBytes - 4L - image.Frame.ByteLength)
                throw new ArgumentException("CalibrationExportFramePayloadOversized");
            var pixels = image.GetBytes();
            if (pixels.Length != image.Frame.ByteLength)
                throw new ArgumentException("CalibrationExportFrameLengthMismatch");
            writer.Int32(manifest.Length);
            writer.Bytes(manifest);
            writer.Int32(pixels.Length);
            writer.Bytes(pixels);
        }
        return writer.ToArray();
    }

    private static IReadOnlyList<CalibrationFrameImage> DecodeFrameImages(byte[] payload)
    {
        var reader = new ExportReader(payload);
        var count = reader.Count(64);
        var images = new List<CalibrationFrameImage>(count);
        string? previousId = null;
        for (var index = 0; index < count; index++)
        {
            var manifest = reader.Bytes(reader.Int32(), CalibrationSessionStorageCodec.MaximumEncodedChars);
            var pixels = reader.Bytes(reader.Int32(), 64 * 1024 * 1024);
            var frame = CalibrationSessionStorageCodec.DecodeFrameManifest(manifest);
            var id = frame.FrameId.ToString("D");
            if (previousId is not null && string.CompareOrdinal(id, previousId) <= 0)
                throw Invalid("CalibrationExportFrameOrderInvalid");
            previousId = id;
            images.Add(new CalibrationFrameImage(frame, pixels));
        }
        reader.EnsureEnd();
        if (images.Count == 0)
            throw Invalid("CalibrationExportFrameSetInvalid");
        return new ReadOnlyCollection<CalibrationFrameImage>(images);
    }

    private static void ValidateDecodedContents(CalibrationExportManifest manifest,
        CalibrationSessionEvidence evidence, CalibrationAcceptancePolicy policy,
        IReadOnlyList<CalibrationFrameImage> images,
        PublishedCalibrationProfileVersion? profile)
    {
        CalibrationSessionStorageCodec.ValidateTransferEvidenceForExport(evidence);
        var temporary = evidence.TemporaryConfiguration ??
            throw Invalid("CalibrationExportTemporaryConfigurationMissing");
        if (manifest.Source.SessionId != evidence.Header.SessionId ||
            (manifest.Source.Candidate is not null) != (evidence.Candidate is not null) ||
            manifest.Procedure != evidence.Header.Command.Plan.Procedure.Procedure ||
            manifest.AcceptancePolicy != policy.Reference ||
            evidence.Header.Command.Plan.Requirement.AcceptancePolicy != policy.Reference ||
            manifest.Device.ContentHash != evidence.Header.Binding.Target.ContentHash ||
            manifest.ImagingSetup != evidence.Header.Command.ExpectedImagingSetup ||
            manifest.RequestedGeometry.ContentHash !=
                CalibrationFrameGeometry.FromRequested(temporary.Requested).ContentHash ||
            manifest.EffectiveGeometry.ContentHash !=
                CalibrationFrameGeometry.FromEffective(temporary.Effective).ContentHash)
            throw Invalid("CalibrationExportManifestCompatibilityMismatch");

        if (manifest.Source.Candidate is { } candidate)
        {
            if (evidence.Candidate is not { } candidateEvidence ||
                candidate.SessionId != candidateEvidence.SessionId ||
                candidate.CandidateId != candidateEvidence.CandidateId ||
                candidate.CandidateContentHash != candidateEvidence.ContentHash)
                throw Invalid("CalibrationExportCandidateIdentityMismatch");
        }
        if ((profile is null) != (manifest.Source.Profile is null))
            throw Invalid("CalibrationExportProfileManifestMismatch");
        if (profile is not null)
        {
            if (evidence.Candidate is not { } candidateEvidence ||
                profile.SourceCandidate.SessionId != candidateEvidence.SessionId ||
                profile.SourceCandidate.CandidateId != candidateEvidence.CandidateId ||
                profile.SourceCandidate.CandidateContentHash != candidateEvidence.ContentHash ||
                profile.Content.SourceEvidenceHash != candidateEvidence.ContentHash ||
                profile.Content.Coefficients.ContentHash !=
                    candidateEvidence.Result.Coefficients.ContentHash ||
                profile.Content.Requirement.ContentHash !=
                    evidence.Header.Command.Plan.Requirement.ContentHash ||
                profile.Content.Procedure != evidence.Header.Command.Plan.Procedure.Procedure ||
                profile.Reference != manifest.Source.Profile ||
                profile.AcceptancePolicy != policy.Reference ||
                profile.Content.Device.ContentHash != manifest.Device.ContentHash ||
                profile.Content.ImagingSetup != manifest.ImagingSetup ||
                profile.Content.RequestedGeometry.ContentHash != manifest.RequestedGeometry.ContentHash ||
                profile.Content.EffectiveGeometry.ContentHash != manifest.EffectiveGeometry.ContentHash)
                throw Invalid("CalibrationExportProfileSourceMismatch");
        }

        var expectedFrames = evidence.Frames.ToDictionary(frame => frame.FrameId);
        if (images.Count != expectedFrames.Count || images.Any(image =>
                !expectedFrames.TryGetValue(image.Frame.FrameId, out var frame) ||
                image.Frame.SourceHash != frame.SourceHash || image.Frame.PixelHash != frame.PixelHash))
            throw Invalid("CalibrationExportFrameEvidenceMismatch");

        var unsigned = RebuildManifest(manifest, ZeroHash);
        if (!string.Equals(manifest.ContentHash, Hash(EncodeManifest(unsigned)),
                StringComparison.Ordinal))
            throw Invalid("CalibrationExportManifestContentHashMismatch");
        var canonical = EncodeManifest(manifest);
        if (canonical.Length > MaximumManifestBytes)
            throw Invalid("CalibrationExportManifestOversized");
    }

    private static CalibrationExportManifest RebuildManifest(
        CalibrationExportManifest value, string contentHash) =>
        new(value.FormatVersion, value.PackageId, value.SourceStationId, value.Source,
            value.Procedure, value.AcceptancePolicy, value.Device, value.ImagingSetup,
            value.RequestedGeometry, value.EffectiveGeometry, value.Members, contentHash);

    private static byte[] EncodeManifest(CalibrationExportManifest value)
    {
        var writer = new ExportWriter(MaximumManifestBytes);
        writer.Int32(ManifestMagic);
        writer.Int32(value.FormatVersion);
        writer.Guid(value.PackageId);
        writer.String(value.SourceStationId, MaximumTextBytes);
        writer.Guid(value.Source.SessionId);
        writer.Bool(value.Source.Candidate is not null);
        if (value.Source.Candidate is { } candidate)
            WriteCandidateReference(writer, candidate);
        writer.Bool(value.Source.Profile is not null);
        if (value.Source.Profile is { } profile)
            WriteProfileReference(writer, profile);
        WriteContract(writer, value.Procedure);
        WriteContract(writer, value.AcceptancePolicy);
        WriteBindingTarget(writer, value.Device);
        WriteImagingSetup(writer, value.ImagingSetup);
        WriteGeometry(writer, value.RequestedGeometry);
        WriteGeometry(writer, value.EffectiveGeometry);
        writer.Count(value.Members.Count, 4);
        foreach (var member in value.Members)
        {
            writer.Byte((byte)member.Kind);
            writer.Int32(member.Length);
            writer.String(member.ContentHash, 64);
        }
        writer.String(value.ContentHash, 64);
        return writer.ToArray();
    }

    private static CalibrationExportManifest DecodeManifest(byte[] payload)
    {
        var reader = new ExportReader(payload);
        if (reader.Int32() != ManifestMagic)
            throw Invalid("CalibrationExportManifestVersionUnsupported");
        var formatVersion = reader.Int32();
        if (formatVersion != CalibrationExportManifest.CurrentFormatVersion)
            throw Invalid("CalibrationExportManifestVersionUnsupported");
        var packageId = reader.Guid();
        var sourceStationId = reader.String(MaximumTextBytes);
        var sessionId = reader.Guid();
        var candidate = reader.Bool() ? ReadCandidateReference(reader) : null;
        var profile = reader.Bool() ? ReadProfileReference(reader) : null;
        var procedure = ReadContract(reader);
        var policy = ReadContract(reader);
        var device = ReadBindingTarget(reader);
        var imaging = ReadImagingSetup(reader);
        var requested = ReadGeometry(reader);
        var effective = ReadGeometry(reader);
        var members = new List<CalibrationExportMemberManifest>(reader.Count(4));
        CalibrationExportMemberKind? previousKind = null;
        foreach (var _ in Enumerable.Range(0, members.Capacity))
        {
            var kind = reader.ByteEnum<CalibrationExportMemberKind>();
            if (previousKind is { } previous && kind <= previous)
                throw Invalid("CalibrationExportMemberManifestOrderInvalid");
            previousKind = kind;
            var length = reader.Int32();
            var hash = reader.String(64);
            members.Add(new CalibrationExportMemberManifest(kind, length, hash));
        }
        var contentHash = reader.String(64);
        reader.EnsureEnd();
        var manifest = new CalibrationExportManifest(formatVersion, packageId, sourceStationId,
            new CalibrationExportSourceIdentity(sessionId, candidate, profile), procedure, policy,
            device, imaging, requested, effective, members, contentHash);
        var unsigned = RebuildManifest(manifest, ZeroHash);
        if (!string.Equals(contentHash, Hash(EncodeManifest(unsigned)), StringComparison.Ordinal))
            throw Invalid("CalibrationExportManifestContentHashMismatch");
        if (!payload.SequenceEqual(EncodeManifest(manifest)))
            throw Invalid("CalibrationExportManifestCanonicalMismatch");
        return manifest;
    }

    private static void WriteCandidateReference(ExportWriter writer,
        CalibrationCandidateReference value)
    {
        writer.Guid(value.SessionId);
        writer.Guid(value.CandidateId);
        writer.String(value.CandidateContentHash, 64);
    }

    private static CalibrationCandidateReference ReadCandidateReference(ExportReader reader) =>
        new(reader.Guid(), reader.Guid(), reader.String(64));

    private static void WriteProfileReference(ExportWriter writer,
        CalibrationProfileReference value)
    {
        writer.Guid(value.ProfileId);
        writer.Int64(value.Version);
        writer.String(value.ContentHash, 64);
    }

    private static CalibrationProfileReference ReadProfileReference(ExportReader reader) =>
        new(reader.Guid(), reader.Int64(), reader.String(64));

    private static void WriteContract(ExportWriter writer, RecipeContractReference value)
    {
        writer.String(value.Id, MaximumTextBytes);
        writer.String(value.Version, MaximumTextBytes);
        writer.String(value.ContentHash, 64);
    }

    private static RecipeContractReference ReadContract(ExportReader reader) =>
        new(reader.String(MaximumTextBytes), reader.String(MaximumTextBytes), reader.String(64));

    private static void WriteBindingTarget(ExportWriter writer, CameraBindingTarget value)
    {
        writer.String(value.Provider.Id, MaximumTextBytes);
        writer.String(value.Provider.Version, MaximumTextBytes);
        writer.String(value.Provider.AdapterPackageId, MaximumTextBytes);
        writer.String(value.Provider.AdapterVersion, MaximumTextBytes);
        writer.String(value.StableDeviceIdentity, MaximumTextBytes);
    }

    private static CameraBindingTarget ReadBindingTarget(ExportReader reader) =>
        new(new CameraProviderIdentity(reader.String(MaximumTextBytes),
                reader.String(MaximumTextBytes), reader.String(MaximumTextBytes),
                reader.String(MaximumTextBytes)), reader.String(MaximumTextBytes));

    private static void WriteImagingSetup(ExportWriter writer,
        ImagingSetupRevisionReference value)
    {
        writer.String(value.LogicalCameraRole, MaximumTextBytes);
        writer.Guid(value.RevisionId);
        writer.Int64(value.Revision);
        writer.String(value.RevisionHash, 64);
    }

    private static ImagingSetupRevisionReference ReadImagingSetup(ExportReader reader) =>
        new(reader.String(MaximumTextBytes), reader.Guid(), reader.Int64(), reader.String(64));

    private static void WriteGeometry(ExportWriter writer, CalibrationFrameGeometry value)
    {
        writer.Int32(value.RegionOfInterest.OffsetX);
        writer.Int32(value.RegionOfInterest.OffsetY);
        writer.Int32(value.RegionOfInterest.Width);
        writer.Int32(value.RegionOfInterest.Height);
        writer.Int32(value.FrameWidth);
        writer.Int32(value.FrameHeight);
        writer.Int32((int)value.PixelFormat);
        writer.Bool(value.ValidBits is not null);
        if (value.ValidBits is { } validBits)
            writer.Int32(validBits);
    }

    private static CalibrationFrameGeometry ReadGeometry(ExportReader reader)
    {
        var roi = new RegionOfInterest(reader.Int32(), reader.Int32(), reader.Int32(), reader.Int32());
        return new(roi, reader.Int32(), reader.Int32(), reader.Enum<VisionPixelFormat>(),
            reader.Bool() ? reader.Int32() : null);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static ArgumentException Invalid(string reason, Exception? inner = null) => new(reason, inner);

    private sealed record MemberPayload(CalibrationExportMemberKind Kind, byte[] Payload);

    internal sealed class CalibrationExportPackageContents
    {
        internal CalibrationExportPackageContents(CalibrationExportManifest manifest,
            CalibrationSessionEvidence evidence, CalibrationAcceptancePolicy policy,
            PublishedCalibrationProfileVersion? profile,
            IReadOnlyList<CalibrationFrameImage> images)
        {
            Manifest = manifest;
            Evidence = evidence;
            Policy = policy;
            Profile = profile;
            Images = images;
        }

        public CalibrationExportManifest Manifest { get; }
        public CalibrationSessionEvidence Evidence { get; }
        public CalibrationAcceptancePolicy Policy { get; }
        public PublishedCalibrationProfileVersion? Profile { get; }
        public IReadOnlyList<CalibrationFrameImage> Images { get; }
    }

    private sealed class ExportWriter
    {
        private readonly MemoryStream _stream = new();
        private readonly int _maximumBytes;

        internal ExportWriter(int maximumBytes) => _maximumBytes = maximumBytes;

        internal void Byte(byte value)
        {
            if (_stream.Length >= _maximumBytes)
                throw new ArgumentException("CalibrationExportPackageSizeInvalid");
            _stream.WriteByte(value);
        }

        internal void UInt16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            Bytes(buffer);
        }

        internal void Int32(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            Bytes(buffer);
        }

        internal void Int64(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            Bytes(buffer);
        }

        internal void Guid(Guid value)
        {
            Span<byte> buffer = stackalloc byte[16];
            if (!value.TryWriteBytes(buffer))
                throw new InvalidOperationException("CalibrationExportGuidEncodingFailed");
            Bytes(buffer);
        }

        internal void Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

        internal void String(string value, int maximumBytes)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = new UTF8Encoding(false, true).GetBytes(value);
            if (bytes.Length > maximumBytes)
                throw new ArgumentException("CalibrationExportTextOversized", nameof(value));
            Int32(bytes.Length);
            Bytes(bytes);
        }

        internal void Count(int value, int maximum)
        {
            if (value < 0 || value > maximum)
                throw new ArgumentOutOfRangeException(nameof(value));
            Int32(value);
        }

        internal long Length => _stream.Length;

        internal void Bytes(ReadOnlySpan<byte> value)
        {
            if (value.Length > _maximumBytes - _stream.Length)
                throw new ArgumentException("CalibrationExportPackageSizeInvalid");
            _stream.Write(value);
        }

        internal byte[] ToArray() => _stream.ToArray();
    }

    private sealed class ExportReader
    {
        private readonly byte[] _bytes;
        private int _position;

        internal ExportReader(byte[] bytes) => _bytes = bytes;

        internal byte Byte()
        {
            Need(1);
            return _bytes[_position++];
        }

        internal ushort UInt16()
        {
            var bytes = Bytes(2);
            return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        }

        internal int Int32()
        {
            var bytes = Bytes(4);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }

        internal long Int64()
        {
            var bytes = Bytes(8);
            return BinaryPrimitives.ReadInt64LittleEndian(bytes);
        }

        internal Guid Guid() => new(Bytes(16));

        internal bool Bool() => Byte() switch
        {
            0 => false,
            1 => true,
            _ => throw Invalid("CalibrationExportBooleanInvalid")
        };

        internal string String(int maximumBytes)
        {
            var length = Int32();
            var bytes = Bytes(length, maximumBytes);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException exception)
            {
                throw Invalid("CalibrationExportTextInvalid", exception);
            }
        }

        internal T Enum<T>() where T : struct, System.Enum
        {
            var value = Int32();
            return ParseEnum<T>(value, value);
        }

        internal T ByteEnum<T>() where T : struct, System.Enum
        {
            var value = Byte();
            return ParseEnum<T>(value, value);
        }

        private T ParseEnum<T>(object encodedValue, long encodedNumeric)
            where T : struct, System.Enum
        {
            var parsed = System.Enum.ToObject(typeof(T), encodedValue);
            if (!System.Enum.IsDefined(typeof(T), parsed))
                throw Invalid("CalibrationExportEnumInvalid");

            long parsedNumeric;
            try
            {
                parsedNumeric = Convert.ToInt64(parsed);
            }
            catch (Exception exception) when (exception is InvalidCastException or OverflowException)
            {
                throw Invalid("CalibrationExportEnumInvalid", exception);
            }

            if (parsedNumeric != encodedNumeric || parsed is not T typed)
                throw Invalid("CalibrationExportEnumInvalid");

            return typed;
        }

        internal int Count(int maximum)
        {
            var count = Int32();
            if (count < 0 || count > maximum)
                throw Invalid("CalibrationExportCountInvalid");
            return count;
        }

        internal byte[] Bytes(int length, int maximumLength = MaximumBytes)
        {
            if (length < 0 || length > maximumLength)
                throw Invalid("CalibrationExportMemberSizeInvalid");
            Need(length);
            var result = _bytes.AsSpan(_position, length).ToArray();
            _position += length;
            return result;
        }

        internal void EnsureEnd()
        {
            if (_position != _bytes.Length)
                throw Invalid("CalibrationExportTrailingBytes");
        }

        private void Need(int count)
        {
            if (count < 0 || count > _bytes.Length - _position)
                throw Invalid("CalibrationExportPayloadTruncated");
        }
    }
}
