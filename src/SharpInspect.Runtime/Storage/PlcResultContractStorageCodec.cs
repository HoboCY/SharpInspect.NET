using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>Strict, self-contained wire format for one immutable PLC result-contract revision.</summary>
internal static class PlcResultContractStorageCodec
{
    internal const int MaximumPayloadBytes = PlcResultContractStoreOptions.MaximumPayloadBytesHardLimit;
    private const int Magic = 0x31524350; // PCR1
    private const byte LegacyFormatVersion = 1;
    private const byte ReasonCatalogFormatVersion = 2;
    private const int MaximumStringBytes = 256 * 1024;
    private const int MaximumContractStringBytes = 64;
    private const int MaximumSchemaCount = 64;
    private const int MaximumFieldCount = 256;
    private const int MaximumCodeCount = 64 * 256 + 6;
    private const int MaximumCheckCount = 4096;
    private const int MaximumBindingCount = 10000;

    internal static byte[] Encode(PlcResultContractRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(Magic);
            var formatVersion = IsLegacyReasonCatalog(revision.Contract)
                ? LegacyFormatVersion : ReasonCatalogFormatVersion;
            writer.Write(formatVersion);
            WriteRevision(writer, revision, formatVersion);
            WriteString(writer, revision.ContentHash, MaximumContractStringBytes);
        }
        if (stream.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("PlcResultContractPayloadCapacityExceeded");
        return stream.ToArray();
    }

    internal static PlcResultContractRevision Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("PlcResultContractPayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
            if (reader.ReadInt32() != Magic)
                throw new InvalidOperationException("PlcResultContractPayloadVersionUnsupported");
            var formatVersion = reader.ReadByte();
            if (formatVersion is not (LegacyFormatVersion or ReasonCatalogFormatVersion))
                throw new InvalidOperationException("PlcResultContractPayloadVersionUnsupported");
            var revision = ReadRevision(reader, formatVersion);
            var savedHash = ReadString(reader, MaximumContractStringBytes);
            if (!string.Equals(savedHash, revision.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("PlcResultContractContentHashMismatch");
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("PlcResultContractPayloadTrailingBytes");
            if (!payload.Span.SequenceEqual(Encode(revision)))
                throw new InvalidOperationException("PlcResultContractPayloadCanonicalMismatch");
            return revision;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not InvalidOperationException
            { Message: "PlcResultContractPayloadCapacityExceeded" })
        {
            throw new InvalidOperationException("PlcResultContractPayloadInvalid", exception);
        }
    }

    private static void WriteRevision(BinaryWriter writer, PlcResultContractRevision revision,
        byte formatVersion)
    {
        writer.Write(revision.Position);
        writer.Write(revision.RevisionId.ToByteArray());
        writer.Write(revision.OperationId.ToByteArray());
        WriteContract(writer, revision.Contract, formatVersion == ReasonCatalogFormatVersion);
        WriteOptionalContract(writer, revision.PreviousContract);
        writer.Write(revision.ReleaseHighWatermark);
        WriteCount(writer, revision.SchemaValidations.Count, MaximumSchemaCount);
        foreach (var validation in revision.SchemaValidations)
        {
            WriteSchema(writer, validation.Schema);
            WriteCount(writer, validation.Checks.Count, MaximumCheckCount);
            foreach (var check in validation.Checks)
            {
                WriteString(writer, check.ValidationId, MaximumStringBytes);
                WriteString(writer, check.Subject, MaximumStringBytes);
                writer.Write(check.Passed);
                WriteString(writer, check.ReasonCode, MaximumStringBytes);
                WriteString(writer, check.ContentHash, MaximumContractStringBytes);
            }
            WriteString(writer, validation.ContentHash, MaximumContractStringBytes);
        }
        WriteCount(writer, revision.Bindings.Count, MaximumBindingCount);
        foreach (var binding in revision.Bindings)
        {
            writer.Write(binding.ReleaseId.ToByteArray());
            WriteString(writer, binding.ReleaseRecordContentHash, MaximumContractStringBytes);
            WriteString(writer, binding.Binding.Recipe.Id, MaximumStringBytes);
            WriteString(writer, binding.Binding.Recipe.Version, MaximumStringBytes);
            WriteString(writer, binding.Binding.Recipe.ContentHash, MaximumContractStringBytes);
            WriteString(writer, binding.Binding.Algorithm.Id, MaximumStringBytes);
            WriteString(writer, binding.Binding.Algorithm.Version, MaximumStringBytes);
            WriteString(writer, binding.Binding.Validation.ContentHash, MaximumContractStringBytes);
            WriteString(writer, binding.Binding.ContentHash, MaximumContractStringBytes);
            WriteString(writer, binding.ContentHash, MaximumContractStringBytes);
        }
        writer.Write(revision.ActorPrincipalId.ToByteArray());
        writer.Write(revision.ActorSessionId.ToByteArray());
        writer.Write(revision.ActorAuthorizationRevision);
        writer.Write(revision.StepUpGrantId.ToByteArray());
        WriteContractReference(writer, revision.AuthorizationPolicy);
        WriteString(writer, revision.ChangeReason, MaximumStringBytes);
        WriteString(writer, revision.AuthorizationTarget, MaximumContractStringBytes);
        writer.Write(revision.RecordedAtUtc.UtcTicks);
    }

    private static PlcResultContractRevision ReadRevision(BinaryReader reader, byte formatVersion)
    {
        var position = reader.ReadInt64();
        var revisionId = ReadGuid(reader);
        var operationId = ReadGuid(reader);
        var contract = ReadContract(reader, formatVersion == ReasonCatalogFormatVersion);
        var previous = ReadOptionalContract(reader);
        var releaseHighWatermark = reader.ReadInt64();
        var validations = new List<PlcResultSchemaValidation>(ReadCount(reader, MaximumSchemaCount));
        for (var index = 0; index < validations.Capacity; index++)
        {
            var schema = ReadSchema(reader);
            var checks = new List<PlcResultValidationCheck>(ReadCount(reader, MaximumCheckCount));
            for (var checkIndex = 0; checkIndex < checks.Capacity; checkIndex++)
            {
                var check = new PlcResultValidationCheck(ReadString(reader, MaximumStringBytes),
                    ReadString(reader, MaximumStringBytes), reader.ReadBoolean(),
                    ReadString(reader, MaximumStringBytes));
                var savedCheckHash = ReadString(reader, MaximumContractStringBytes);
                if (!string.Equals(savedCheckHash, check.ContentHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("PlcResultContractCheckHashMismatch");
                checks.Add(check);
            }
            var validation = new PlcResultSchemaValidation(contract, schema, checks);
            var savedValidationHash = ReadString(reader, MaximumContractStringBytes);
            if (!string.Equals(savedValidationHash, validation.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("PlcResultContractSchemaValidationHashMismatch");
            validations.Add(validation);
        }
        var bindings = new List<PlcReleasedRecipeBinding>(ReadCount(reader, MaximumBindingCount));
        for (var index = 0; index < bindings.Capacity; index++)
        {
            var releaseId = ReadGuid(reader);
            var releaseHash = ReadString(reader, MaximumContractStringBytes);
            var recipe = new RecipeReference(ReadString(reader, MaximumStringBytes),
                ReadString(reader, MaximumStringBytes), ReadString(reader, MaximumContractStringBytes));
            var algorithm = new AlgorithmIdentity(ReadString(reader, MaximumStringBytes),
                ReadString(reader, MaximumStringBytes));
            var validationHash = ReadString(reader, MaximumContractStringBytes);
            var validation = validations.SingleOrDefault(value => value.ContentHash == validationHash)
                ?? throw new InvalidOperationException("PlcResultContractBindingValidationMissing");
            var binding = new PlcResultContractBinding(recipe, algorithm, validation);
            var savedBindingHash = ReadString(reader, MaximumContractStringBytes);
            if (!string.Equals(savedBindingHash, binding.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("PlcResultContractBindingHashMismatch");
            var result = new PlcReleasedRecipeBinding(releaseId, releaseHash, binding);
            var savedResultHash = ReadString(reader, MaximumContractStringBytes);
            if (!string.Equals(savedResultHash, result.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("PlcResultContractReleasedRecipeBindingHashMismatch");
            bindings.Add(result);
        }
        var actorPrincipalId = ReadGuid(reader);
        var actorSessionId = ReadGuid(reader);
        var actorAuthorizationRevision = reader.ReadInt64();
        var stepUpGrantId = ReadGuid(reader);
        var authorizationPolicy = ReadContractReference(reader);
        var changeReason = ReadString(reader, MaximumStringBytes);
        var authorizationTarget = ReadString(reader, MaximumContractStringBytes);
        DateTimeOffset recordedAtUtc;
        try { recordedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException exception)
        { throw new InvalidOperationException("PlcResultContractTimestampInvalid", exception); }
        return new PlcResultContractRevision(position, revisionId, operationId, contract, previous,
            releaseHighWatermark, validations, bindings, actorPrincipalId, actorSessionId,
            actorAuthorizationRevision, stepUpGrantId, authorizationPolicy, changeReason,
            authorizationTarget, recordedAtUtc);
    }

    private static void WriteContract(BinaryWriter writer, PlcResultContract contract,
        bool writeReasonCatalog)
    {
        WriteString(writer, contract.Id, MaximumStringBytes);
        WriteString(writer, contract.Version, MaximumStringBytes);
        writer.Write(contract.MaximumPayloadBytes);
        writer.Write(contract.MaximumRegisterCount);
        if (writeReasonCatalog) WriteReasonCatalog(writer, contract.FrameworkReasonCatalogDefinition);
        WriteCount(writer, contract.FrameworkFields.Count, 5);
        foreach (var field in contract.FrameworkFields) WriteFrameworkField(writer, field);
        WriteCount(writer, contract.SchemaMaps.Count, MaximumSchemaCount);
        foreach (var map in contract.SchemaMaps) WriteSchemaMap(writer, map);
    }

    private static PlcResultContract ReadContract(BinaryReader reader, bool readReasonCatalog)
    {
        var id = ReadString(reader, MaximumStringBytes);
        var version = ReadString(reader, MaximumStringBytes);
        var maximumPayloadBytes = reader.ReadInt32();
        var maximumRegisterCount = reader.ReadInt32();
        var reasonCatalog = readReasonCatalog ? ReadReasonCatalog(reader) : null;
        var fields = new List<PlcFrameworkFieldMapping>(ReadCount(reader, 5));
        for (var index = 0; index < fields.Capacity; index++) fields.Add(ReadFrameworkField(reader));
        var maps = new List<PlcResultSchemaMap>(ReadCount(reader, MaximumSchemaCount));
        for (var index = 0; index < maps.Capacity; index++) maps.Add(ReadSchemaMap(reader));
        return reasonCatalog is null
            ? new PlcResultContract(id, version, maximumPayloadBytes, maximumRegisterCount, fields, maps)
            : new PlcResultContract(id, version, maximumPayloadBytes, maximumRegisterCount, fields, maps,
                reasonCatalog);
    }

    private static void WriteReasonCatalog(BinaryWriter writer, PlcResultReasonCatalog catalog)
    {
        WriteString(writer, catalog.Id, MaximumStringBytes);
        WriteString(writer, catalog.Version, MaximumStringBytes);
        WriteCount(writer, catalog.Codes.Count, 256);
        foreach (var code in catalog.Codes) WriteString(writer, code, MaximumStringBytes);
        WriteString(writer, catalog.ContentHash, MaximumContractStringBytes);
    }

    private static PlcResultReasonCatalog ReadReasonCatalog(BinaryReader reader)
    {
        var catalog = new PlcResultReasonCatalog(ReadString(reader, MaximumStringBytes),
            ReadString(reader, MaximumStringBytes),
            Enumerable.Range(0, ReadCount(reader, 256))
                .Select(_ => ReadString(reader, MaximumStringBytes)));
        var savedHash = ReadString(reader, MaximumContractStringBytes);
        if (!string.Equals(savedHash, catalog.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PlcResultReasonCatalogHashMismatch");
        return catalog;
    }

    private static bool IsLegacyReasonCatalog(PlcResultContract contract) =>
        contract.UsesLegacyReasonCatalogWireFormat &&
        contract.FrameworkReasonCatalogDefinition.Id == PlcResultContract.FrameworkReasonCatalogId &&
        contract.FrameworkReasonCatalogDefinition.Version == PlcResultContract.FrameworkReasonCatalogVersion &&
        contract.FrameworkReasonCatalog.SequenceEqual(PlcResultContract.FrameworkReasonCodes,
            StringComparer.Ordinal);

    private static void WriteContractReference(BinaryWriter writer, RecipeContractReference reference)
    {
        WriteString(writer, reference.Id, MaximumStringBytes);
        WriteString(writer, reference.Version, MaximumStringBytes);
        WriteString(writer, reference.ContentHash, MaximumContractStringBytes);
    }

    private static RecipeContractReference ReadContractReference(BinaryReader reader) =>
        new(ReadString(reader, MaximumStringBytes), ReadString(reader, MaximumStringBytes),
            ReadString(reader, MaximumContractStringBytes));

    private static void WriteOptionalContract(BinaryWriter writer, RecipeContractReference? reference)
    {
        writer.Write(reference is not null);
        if (reference is not null) WriteContractReference(writer, reference);
    }

    private static RecipeContractReference? ReadOptionalContract(BinaryReader reader) =>
        reader.ReadBoolean() ? ReadContractReference(reader) : null;

    private static void WriteFrameworkField(BinaryWriter writer, PlcFrameworkFieldMapping field)
    {
        writer.Write((byte)field.Field);
        WriteRange(writer, field.RegisterRange);
        WriteEncoding(writer, field.Encoding);
        switch (field.Field)
        {
            case PlcFrameworkResultField.ExecutionStatus:
                WriteCount(writer, field.ExecutionStatusCodes.Count, 256);
                foreach (var code in field.ExecutionStatusCodes)
                { writer.Write((int)code.Value); writer.Write(code.LongWireCode); }
                break;
            case PlcFrameworkResultField.InspectionDecision:
                WriteCount(writer, field.InspectionDecisionCodes.Count, 256);
                foreach (var code in field.InspectionDecisionCodes)
                { writer.Write((int)code.Value); writer.Write(code.LongWireCode); }
                break;
            case PlcFrameworkResultField.ResultReasonCode:
                WriteCount(writer, field.ReasonCodes.Count, MaximumCodeCount);
                foreach (var code in field.ReasonCodes)
                { WriteOptionalString(writer, code.ReasonCode, MaximumStringBytes); writer.Write(code.LongWireCode); }
                break;
            default:
                writer.Write(0);
                break;
        }
        WriteString(writer, field.ContentHash, MaximumContractStringBytes);
    }

    private static PlcFrameworkFieldMapping ReadFrameworkField(BinaryReader reader)
    {
        var field = ReadEnum<PlcFrameworkResultField>(reader.ReadByte(), "PlcResultContractFrameworkFieldInvalid");
        var range = ReadRange(reader);
        var encoding = ReadEncoding(reader);
        var execution = new List<PlcExecutionStatusCode>();
        var decisions = new List<PlcInspectionDecisionCode>();
        var reasons = new List<PlcReasonCode>();
        var count = ReadCount(reader, field == PlcFrameworkResultField.ResultReasonCode ? MaximumCodeCount : 256);
        for (var index = 0; index < count; index++)
        {
            switch (field)
            {
                case PlcFrameworkResultField.ExecutionStatus:
                    execution.Add(new PlcExecutionStatusCode(ReadEnum<ExecutionStatus>(reader.ReadInt32(),
                        "PlcResultContractStatusInvalid"), reader.ReadInt64()));
                    break;
                case PlcFrameworkResultField.InspectionDecision:
                    decisions.Add(new PlcInspectionDecisionCode(ReadEnum<InspectionDecision>(reader.ReadInt32(),
                        "PlcResultContractDecisionInvalid"), reader.ReadInt64()));
                    break;
                case PlcFrameworkResultField.ResultReasonCode:
                    reasons.Add(new PlcReasonCode(ReadOptionalString(reader, MaximumStringBytes), reader.ReadInt64()));
                    break;
                default:
                    throw new InvalidOperationException("PlcResultContractFrameworkTableInvalid");
            }
        }
        var result = field switch
        {
            PlcFrameworkResultField.ExecutionStatus => new PlcFrameworkFieldMapping(field, range, encoding,
                executionStatusCodes: execution, inspectionDecisionCodes: null, reasonCodes: null),
            PlcFrameworkResultField.InspectionDecision => new PlcFrameworkFieldMapping(field, range, encoding,
                executionStatusCodes: null, inspectionDecisionCodes: decisions, reasonCodes: null),
            PlcFrameworkResultField.ResultReasonCode => new PlcFrameworkFieldMapping(field, range, encoding,
                executionStatusCodes: null, inspectionDecisionCodes: null, reasonCodes: reasons),
            _ => new PlcFrameworkFieldMapping(field, range, encoding, null, null, null)
        };
        var savedHash = ReadString(reader, MaximumContractStringBytes);
        if (!string.Equals(savedHash, result.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PlcResultContractFrameworkFieldHashMismatch");
        return result;
    }

    private static void WriteSchemaMap(BinaryWriter writer, PlcResultSchemaMap map)
    {
        WriteContractReference(writer, map.ResultSchema);
        WriteCount(writer, map.Measurements.Count, MaximumFieldCount);
        foreach (var measurement in map.Measurements) WriteMeasurement(writer, measurement);
        WriteCount(writer, map.ConstantFields.Count, MaximumFieldCount);
        foreach (var constant in map.ConstantFields) WriteConstant(writer, constant);
        WriteString(writer, map.ContentHash, MaximumContractStringBytes);
    }

    private static PlcResultSchemaMap ReadSchemaMap(BinaryReader reader)
    {
        var reference = ReadContractReference(reader);
        var measurements = new List<PlcMeasurementMapping>(ReadCount(reader, MaximumFieldCount));
        for (var index = 0; index < measurements.Capacity; index++) measurements.Add(ReadMeasurement(reader));
        var constants = new List<PlcConstantField>(ReadCount(reader, MaximumFieldCount));
        for (var index = 0; index < constants.Capacity; index++) constants.Add(ReadConstant(reader));
        var result = new PlcResultSchemaMap(reference, measurements, constants);
        var savedHash = ReadString(reader, MaximumContractStringBytes);
        if (!string.Equals(savedHash, result.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PlcResultContractSchemaMapHashMismatch");
        return result;
    }

    private static void WriteMeasurement(BinaryWriter writer, PlcMeasurementMapping mapping)
    {
        WriteString(writer, mapping.FieldKey, MaximumStringBytes);
        writer.Write((byte)mapping.Disposition);
        writer.Write(mapping.RegisterRange is not null);
        if (mapping.RegisterRange is not null) WriteRange(writer, mapping.RegisterRange);
        writer.Write(mapping.Conversion is not null);
        if (mapping.Conversion is not null) WriteConversion(writer, mapping.Conversion);
        writer.Write(mapping.Encoding is not null);
        if (mapping.Encoding is not null) WriteEncoding(writer, mapping.Encoding);
        WriteOptionalLiteral(writer, mapping.NonSuccessData);
        WriteOptionalAbsence(writer, mapping.OptionalAbsence);
        WriteCount(writer, mapping.CodeTable.Count, 256);
        foreach (var code in mapping.CodeTable) WriteScalarCode(writer, code);
        WriteString(writer, mapping.ContentHash, MaximumContractStringBytes);
    }

    private static PlcMeasurementMapping ReadMeasurement(BinaryReader reader)
    {
        var key = ReadString(reader, MaximumStringBytes);
        var disposition = ReadEnum<PlcMeasurementDisposition>(reader.ReadByte(), "PlcResultContractMeasurementDispositionInvalid");
        var range = reader.ReadBoolean() ? ReadRange(reader) : null;
        var conversion = reader.ReadBoolean() ? ReadConversion(reader) : null;
        var encoding = reader.ReadBoolean() ? ReadEncoding(reader) : null;
        var nonSuccess = ReadOptionalLiteral(reader);
        var absence = ReadOptionalAbsence(reader);
        var codes = new List<PlcScalarCode>(ReadCount(reader, 256));
        for (var index = 0; index < codes.Capacity; index++) codes.Add(ReadScalarCode(reader));
        var result = new PlcMeasurementMapping(key, disposition, range, conversion, encoding, nonSuccess, absence,
            codes.Count == 0 ? null : codes);
        var savedHash = ReadString(reader, MaximumContractStringBytes);
        if (!string.Equals(savedHash, result.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PlcResultContractMeasurementHashMismatch");
        return result;
    }

    private static void WriteConstant(BinaryWriter writer, PlcConstantField constant)
    {
        WriteString(writer, constant.FieldKey, MaximumStringBytes);
        WriteRange(writer, constant.RegisterRange);
        WriteEncoding(writer, constant.Encoding);
        WriteLiteral(writer, constant.Value);
        WriteString(writer, constant.ContentHash, MaximumContractStringBytes);
    }

    private static PlcConstantField ReadConstant(BinaryReader reader)
    {
        var result = new PlcConstantField(ReadString(reader, MaximumStringBytes), ReadRange(reader),
            ReadEncoding(reader), ReadLiteral(reader));
        var savedHash = ReadString(reader, MaximumContractStringBytes);
        if (!string.Equals(savedHash, result.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PlcResultContractConstantHashMismatch");
        return result;
    }

    private static void WriteConversion(BinaryWriter writer, PlcAffineConversion conversion)
    {
        WriteString(writer, conversion.SourceUnit, MaximumStringBytes);
        WriteString(writer, conversion.WireUnit, MaximumStringBytes);
        writer.Write(conversion.Multiplier.Numerator); writer.Write(conversion.Multiplier.Denominator);
        writer.Write(conversion.Offset.Numerator); writer.Write(conversion.Offset.Denominator);
    }

    private static PlcAffineConversion ReadConversion(BinaryReader reader) => new(
        ReadString(reader, MaximumStringBytes), ReadString(reader, MaximumStringBytes),
        new PlcRational(reader.ReadInt64(), reader.ReadInt64()),
        new PlcRational(reader.ReadInt64(), reader.ReadInt64()));

    private static void WriteEncoding(BinaryWriter writer, PlcWireEncoding encoding)
    {
        writer.Write((byte)encoding.Representation); writer.Write((byte)encoding.ByteOrder);
        writer.Write((byte)encoding.WordOrder); writer.Write((byte)encoding.Rounding);
        writer.Write((byte)encoding.Overflow);
    }

    private static PlcWireEncoding ReadEncoding(BinaryReader reader) => new(
        ReadEnum<PlcWireRepresentation>(reader.ReadByte(), "PlcResultContractRepresentationInvalid"),
        ReadEnum<PlcByteOrder>(reader.ReadByte(), "PlcResultContractByteOrderInvalid"),
        ReadEnum<PlcWordOrder>(reader.ReadByte(), "PlcResultContractWordOrderInvalid"),
        ReadEnum<PlcRoundingMode>(reader.ReadByte(), "PlcResultContractRoundingInvalid"),
        ReadEnum<PlcOverflowBehavior>(reader.ReadByte(), "PlcResultContractOverflowInvalid"));

    private static void WriteRange(BinaryWriter writer, PlcRegisterRange range)
    { writer.Write(range.StartRegister); writer.Write(range.RegisterCount); }
    private static PlcRegisterRange ReadRange(BinaryReader reader) => new(reader.ReadInt32(), reader.ReadInt32());

    private static void WriteLiteral(BinaryWriter writer, PlcWireLiteral literal)
    {
        var bytes = literal.ToArray(); WriteCount(writer, bytes.Length, PlcWireLiteral.MaximumBytes); writer.Write(bytes);
    }
    private static PlcWireLiteral ReadLiteral(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(ReadCount(reader, PlcWireLiteral.MaximumBytes));
        if (bytes.Length == 0) throw new InvalidOperationException("PlcResultContractLiteralInvalid");
        return new PlcWireLiteral(bytes);
    }
    private static void WriteOptionalLiteral(BinaryWriter writer, PlcWireLiteral? literal)
    { writer.Write(literal is not null); if (literal is not null) WriteLiteral(writer, literal); }
    private static PlcWireLiteral? ReadOptionalLiteral(BinaryReader reader) => reader.ReadBoolean() ? ReadLiteral(reader) : null;

    private static void WriteOptionalAbsence(BinaryWriter writer, PlcOptionalAbsence? absence)
    {
        writer.Write(absence is not null);
        if (absence is null) return;
        writer.Write((byte)absence.Kind);
        if (absence.Kind == PlcOptionalAbsenceKind.Sentinel) WriteLiteral(writer, absence.Sentinel!);
        else
        {
            WriteValidityField(writer, absence.ValidityField!);
            WriteLiteral(writer, absence.AbsentData!);
        }
    }
    private static PlcOptionalAbsence? ReadOptionalAbsence(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var kind = ReadEnum<PlcOptionalAbsenceKind>(reader.ReadByte(), "PlcResultContractAbsenceKindInvalid");
        return kind == PlcOptionalAbsenceKind.Sentinel
            ? new PlcOptionalAbsence(ReadLiteral(reader))
            : new PlcOptionalAbsence(ReadValidityField(reader), ReadLiteral(reader));
    }

    private static void WriteValidityField(BinaryWriter writer, PlcValidityField field)
    {
        WriteString(writer, field.FieldKey, MaximumStringBytes); WriteRange(writer, field.RegisterRange);
        WriteEncoding(writer, field.Encoding); WriteLiteral(writer, field.PresentData); WriteLiteral(writer, field.AbsentData);
    }
    private static PlcValidityField ReadValidityField(BinaryReader reader) => new(
        ReadString(reader, MaximumStringBytes), ReadRange(reader), ReadEncoding(reader), ReadLiteral(reader), ReadLiteral(reader));

    private static void WriteScalarCode(BinaryWriter writer, PlcScalarCode code)
    { WriteScalar(writer, code.Value); writer.Write(code.LongWireCode); }
    private static PlcScalarCode ReadScalarCode(BinaryReader reader) => new(ReadScalar(reader), reader.ReadInt64());

    private static void WriteScalar(BinaryWriter writer, AlgorithmScalarValue value)
    {
        writer.Write((byte)value.Type);
        switch (value.Type)
        {
            case AlgorithmScalarType.Boolean: writer.Write(value.AsBoolean()); break;
            case AlgorithmScalarType.Int64: writer.Write(value.AsInt64()); break;
            case AlgorithmScalarType.Float64: writer.Write(BitConverter.DoubleToInt64Bits(value.AsFloat64())); break;
            case AlgorithmScalarType.String: WriteString(writer, value.AsString(), AlgorithmScalarValue.MaximumTextBytes); break;
            case AlgorithmScalarType.Enum: WriteString(writer, value.AsEnum(), AlgorithmScalarValue.MaximumTextBytes); break;
            default: throw new InvalidOperationException("PlcResultContractScalarTypeInvalid");
        }
    }
    private static AlgorithmScalarValue ReadScalar(BinaryReader reader)
    {
        var type = ReadEnum<AlgorithmScalarType>(reader.ReadByte(), "PlcResultContractScalarTypeInvalid");
        return type switch
        {
            AlgorithmScalarType.Boolean => AlgorithmScalarValue.FromBoolean(reader.ReadBoolean()),
            AlgorithmScalarType.Int64 => AlgorithmScalarValue.FromInt64(reader.ReadInt64()),
            AlgorithmScalarType.Float64 => AlgorithmScalarValue.FromFloat64(BitConverter.Int64BitsToDouble(reader.ReadInt64())),
            AlgorithmScalarType.String => AlgorithmScalarValue.FromString(ReadString(reader, AlgorithmScalarValue.MaximumTextBytes)),
            AlgorithmScalarType.Enum => AlgorithmScalarValue.FromEnum(ReadString(reader, AlgorithmScalarValue.MaximumTextBytes)),
            _ => throw new InvalidOperationException("PlcResultContractScalarTypeInvalid")
        };
    }

    private static void WriteSchema(BinaryWriter writer, AlgorithmResultSchema schema)
    {
        WriteString(writer, schema.Id, MaximumStringBytes); WriteString(writer, schema.Version, MaximumStringBytes);
        WriteCount(writer, schema.Measurements.Count, MaximumFieldCount);
        foreach (var field in schema.Measurements.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            WriteString(writer, field.Key, MaximumStringBytes); writer.Write((byte)field.Type);
            WriteString(writer, field.Unit, MaximumStringBytes); writer.Write(field.Required);
            WriteConstraints(writer, field.Constraints); WriteOptionalScalar(writer, field.AuthoringDefault);
            WriteOptionalString(writer, field.HelpText, MaximumStringBytes);
        }
        WriteCount(writer, schema.ReasonCodes.Count, 256);
        foreach (var reason in schema.ReasonCodes.OrderBy(value => value, StringComparer.Ordinal))
            WriteString(writer, reason, MaximumStringBytes);
        var overlay = schema.OverlayContract;
        WriteString(writer, overlay.Id, MaximumStringBytes); WriteString(writer, overlay.Version, MaximumStringBytes);
        writer.Write(overlay.MaximumElements); writer.Write(overlay.MaximumTotalPoints);
        writer.Write(overlay.MaximumPointsPerElement); writer.Write(overlay.MaximumTextLength);
        WriteString(writer, schema.ContentHash, MaximumContractStringBytes);
    }

    private static AlgorithmResultSchema ReadSchema(BinaryReader reader)
    {
        var id = ReadString(reader, MaximumStringBytes); var version = ReadString(reader, MaximumStringBytes);
        var fields = new List<AlgorithmFieldDefinition>(ReadCount(reader, MaximumFieldCount));
        for (var index = 0; index < fields.Capacity; index++)
        {
            fields.Add(new AlgorithmFieldDefinition(ReadString(reader, MaximumStringBytes),
                ReadEnum<AlgorithmScalarType>(reader.ReadByte(), "PlcResultContractAlgorithmScalarTypeInvalid"),
                ReadString(reader, MaximumStringBytes), reader.ReadBoolean(), ReadConstraints(reader),
                ReadOptionalScalar(reader), ReadOptionalString(reader, MaximumStringBytes)));
        }
        var reasons = new List<string>(ReadCount(reader, 256));
        for (var index = 0; index < reasons.Capacity; index++) reasons.Add(ReadString(reader, MaximumStringBytes));
        var overlay = new OverlayContract(ReadString(reader, MaximumStringBytes), ReadString(reader, MaximumStringBytes),
            reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        var result = new AlgorithmResultSchema(id, version, fields, reasons, overlay);
        var savedHash = ReadString(reader, MaximumContractStringBytes);
        if (!string.Equals(savedHash, result.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PlcResultContractAlgorithmSchemaHashMismatch");
        return result;
    }

    private static void WriteConstraints(BinaryWriter writer, AlgorithmScalarConstraints? constraints)
    {
        writer.Write(constraints is not null);
        if (constraints is null) return;
        WriteOptionalInt64(writer, constraints.MinInt64); WriteOptionalInt64(writer, constraints.MaxInt64);
        WriteOptionalDouble(writer, constraints.MinFloat64); WriteOptionalDouble(writer, constraints.MaxFloat64);
        WriteOptionalInt32(writer, constraints.MinLength); WriteOptionalInt32(writer, constraints.MaxLength);
        writer.Write(constraints.AllowedValues is not null);
        if (constraints.AllowedValues is { } values)
        {
            WriteCount(writer, values.Count, 256);
            foreach (var value in values.OrderBy(value => value, StringComparer.Ordinal))
                WriteString(writer, value, AlgorithmScalarValue.MaximumTextBytes);
        }
    }
    private static AlgorithmScalarConstraints? ReadConstraints(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var minInt = ReadOptionalInt64(reader); var maxInt = ReadOptionalInt64(reader);
        var minFloat = ReadOptionalDouble(reader); var maxFloat = ReadOptionalDouble(reader);
        var minLength = ReadOptionalInt32(reader); var maxLength = ReadOptionalInt32(reader);
        var hasAllowed = reader.ReadBoolean();
        var allowed = hasAllowed ? Enumerable.Range(0, ReadCount(reader, 256))
            .Select(_ => ReadString(reader, AlgorithmScalarValue.MaximumTextBytes)).ToArray() : null;
        return new AlgorithmScalarConstraints(minInt, maxInt, minFloat, maxFloat, minLength, maxLength, allowed);
    }
    private static void WriteOptionalScalar(BinaryWriter writer, AlgorithmScalarValue? value)
    { writer.Write(value is not null); if (value is not null) WriteScalar(writer, value); }
    private static AlgorithmScalarValue? ReadOptionalScalar(BinaryReader reader) => reader.ReadBoolean() ? ReadScalar(reader) : null;

    private static void WriteOptionalInt64(BinaryWriter writer, long? value)
    { writer.Write(value is not null); if (value is not null) writer.Write(value.Value); }
    private static long? ReadOptionalInt64(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt64() : null;
    private static void WriteOptionalInt32(BinaryWriter writer, int? value)
    { writer.Write(value is not null); if (value is not null) writer.Write(value.Value); }
    private static int? ReadOptionalInt32(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt32() : null;
    private static void WriteOptionalDouble(BinaryWriter writer, double? value)
    { writer.Write(value is not null); if (value is not null) writer.Write(BitConverter.DoubleToInt64Bits(value.Value)); }
    private static double? ReadOptionalDouble(BinaryReader reader) => reader.ReadBoolean()
        ? BitConverter.Int64BitsToDouble(reader.ReadInt64()) : null;

    private static void WriteString(BinaryWriter writer, string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes;
        try { bytes = new UTF8Encoding(false, true).GetBytes(value); }
        catch (EncoderFallbackException exception) { throw new InvalidOperationException("PlcResultContractUtf8Invalid", exception); }
        if (bytes.Length > maximumBytes) throw new InvalidOperationException("PlcResultContractStringCapacityExceeded");
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static void WriteOptionalString(BinaryWriter writer, string? value, int maximumBytes)
    { writer.Write(value is not null); if (value is not null) WriteString(writer, value, maximumBytes); }
    private static string ReadString(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximumBytes) throw new InvalidOperationException("PlcResultContractStringCapacityExceeded");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidOperationException("PlcResultContractPayloadTruncated");
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new InvalidOperationException("PlcResultContractUtf8Invalid", exception); }
    }
    private static string? ReadOptionalString(BinaryReader reader, int maximumBytes) => reader.ReadBoolean()
        ? ReadString(reader, maximumBytes) : null;

    private static void WriteCount(BinaryWriter writer, int count, int maximum)
    { if (count < 0 || count > maximum) throw new InvalidOperationException("PlcResultContractCapacityExceeded"); writer.Write(count); }
    private static int ReadCount(BinaryReader reader, int maximum)
    { var count = reader.ReadInt32(); if (count < 0 || count > maximum) throw new InvalidOperationException("PlcResultContractCapacityExceeded"); return count; }
    private static Guid ReadGuid(BinaryReader reader)
    { var bytes = reader.ReadBytes(16); if (bytes.Length != 16) throw new InvalidOperationException("PlcResultContractPayloadTruncated"); var value = new Guid(bytes); if (value == Guid.Empty) throw new InvalidOperationException("PlcResultContractIdentityInvalid"); return value; }
    private static T ReadEnum<T>(int value, string reason) where T : struct, Enum
    {
        // Enum.IsDefined requires the boxed value to use the enum's underlying type.
        // Passing an Int32 for the byte-backed PLC enums raises ArgumentException even
        // when the wire value is valid, which would turn every full framework contract
        // into the generic payload-invalid result during cold replay.
        var underlying = Enum.GetUnderlyingType(typeof(T));
        object underlyingValue = Type.GetTypeCode(underlying) switch
        {
            TypeCode.SByte when value is >= sbyte.MinValue and <= sbyte.MaxValue => (sbyte)value,
            TypeCode.Byte when value is >= byte.MinValue and <= byte.MaxValue => (byte)value,
            TypeCode.Int16 when value is >= short.MinValue and <= short.MaxValue => (short)value,
            TypeCode.UInt16 when value is >= ushort.MinValue and <= ushort.MaxValue => (ushort)value,
            TypeCode.Int32 => value,
            TypeCode.UInt32 when value >= 0 => (uint)value,
            TypeCode.Int64 => (long)value,
            TypeCode.UInt64 when value >= 0 => (ulong)value,
            _ => throw new InvalidOperationException(reason)
        };
        var candidate = Enum.ToObject(typeof(T), underlyingValue);
        return Enum.IsDefined(typeof(T), candidate)
            ? (T)candidate
            : throw new InvalidOperationException(reason);
    }
}
