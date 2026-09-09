using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>Focused wire-storage checks for the immutable schema17 contract ledger.</summary>
public sealed class PlcResultContractStorageTests
{
    [Fact]
    public void V131_S01_CodecUsesSchemaHashCanonicalOrderAndStrictEnvelope()
    {
        var first = Revision(Schema(new[] { "zeta", "alpha" },
            new[] { "ReasonB", "ReasonA" }, new[] { "blue", "amber" }));
        var reordered = Revision(Schema(new[] { "alpha", "zeta" },
            new[] { "ReasonA", "ReasonB" }, new[] { "amber", "blue" }));

        Assert.Equal(first.Contract.ContentHash, reordered.Contract.ContentHash);
        Assert.Equal(first.ContentHash, reordered.ContentHash);
        var payload = PlcResultContractStorageCodec.Encode(first);
        Assert.Equal(payload, PlcResultContractStorageCodec.Encode(reordered));

        var decoded = PlcResultContractStorageCodec.Decode(payload);
        Assert.Equal(first.ContentHash, decoded.ContentHash);
        Assert.Equal(payload, PlcResultContractStorageCodec.Encode(decoded));

        AssertPayloadRejected(payload[..^1]);
        AssertPayloadRejected(payload.Concat(new byte[] { 0x7f }).ToArray());
        var changed = payload.ToArray();
        changed[^1] = changed[^1] == (byte)'A' ? (byte)'B' : (byte)'A';
        AssertPayloadRejected(changed);
    }

    [Fact]
    public void V131_S02_CodecRoundTripsFullBoundFrameworkContractAndReleasedBinding()
    {
        // Use the same complete result schema, five framework mappings, wire mappings,
        // and binder-produced binding used by the payload tests. This exercises every
        // typed framework enum branch in the persisted revision format.
        var schema = PlcResultPayloadTests.Schema();
        var contract = PlcResultPayloadTests.Contract(schema);
        var binding = PlcResultPayloadTests.Bind(schema, contract);
        var releasedBinding = new PlcReleasedRecipeBinding(
            Guid.Parse("f6000000-0000-0000-0000-000000000006"), Hash("released-record"), binding);
        const string reason = "full bound codec round trip";
        var revision = new PlcResultContractRevision(
            1,
            Guid.Parse("f7000000-0000-0000-0000-000000000007"),
            Guid.Parse("f8000000-0000-0000-0000-000000000008"),
            contract,
            null,
            1,
            new[] { binding.Validation },
            new[] { releasedBinding },
            Guid.Parse("f9000000-0000-0000-0000-000000000009"),
            Guid.Parse("fa000000-0000-0000-0000-00000000000a"),
            7,
            Guid.Parse("fb000000-0000-0000-0000-00000000000b"),
            new RecipeContractReference("Storage.Authorization", "1", Hash("policy")),
            reason,
            ChangePlcResultContractCommand.ComputeAuthorizationTarget(contract, null, reason),
            DateTimeOffset.Parse("2026-09-09T00:00:00Z"));

        Assert.Equal(5, revision.Contract.FrameworkFields.Count);
        Assert.Equal(schema.Measurements.Count, revision.Contract.SchemaMaps.Single().Measurements.Count);
        Assert.All(binding.Validation.Checks, check => Assert.True(check.Passed));

        var payload = PlcResultContractStorageCodec.Encode(revision);
        var decoded = PlcResultContractStorageCodec.Decode(payload);

        Assert.Equal(revision.ContentHash, decoded.ContentHash);
        Assert.Equal(revision.Contract.ContentHash, decoded.Contract.ContentHash);
        Assert.Equal(binding.ContentHash, decoded.Bindings.Single().Binding.ContentHash);
        Assert.Equal(payload, PlcResultContractStorageCodec.Encode(decoded));
    }

    private static AlgorithmResultSchema Schema(IReadOnlyList<string> fields,
        IReadOnlyList<string> reasons, IReadOnlyList<string> allowedValues)
    {
        var measurements = fields.Select(field => new AlgorithmFieldDefinition(field,
            AlgorithmScalarType.String, "label", true,
            new AlgorithmScalarConstraints(allowedValues: allowedValues)));
        return new AlgorithmResultSchema("Storage.PlcResult", "1", measurements, reasons,
            new OverlayContract("Storage.PlcOverlay", "1", 0, 0, 0, 0));
    }

    private static PlcResultContractRevision Revision(AlgorithmResultSchema schema)
    {
        var contract = new PlcResultContract("Storage.PlcContract", "1", 1024, 64,
            Array.Empty<PlcFrameworkFieldMapping>(), new[]
            {
                new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash))
            });
        var validation = new PlcResultSchemaValidation(contract, schema, new[]
        {
            new PlcResultValidationCheck("storage-proof", schema.Id, true, "Passed")
        });
        var operationId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
        const string reason = "canonical storage";
        var target = ChangePlcResultContractCommand.ComputeAuthorizationTarget(contract, null, reason);
        return new PlcResultContractRevision(1,
            Guid.Parse("b2000000-0000-0000-0000-000000000002"), operationId, contract, null, 0,
            new[] { validation }, Array.Empty<PlcReleasedRecipeBinding>(),
            Guid.Parse("c3000000-0000-0000-0000-000000000003"),
            Guid.Parse("d4000000-0000-0000-0000-000000000004"), 1,
            Guid.Parse("e5000000-0000-0000-0000-000000000005"),
            new RecipeContractReference("Storage.Authorization", "1", Hash("policy")),
            reason, target, DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
    }

    private static void AssertPayloadRejected(byte[] payload) =>
        Assert.Throws<InvalidOperationException>(() => PlcResultContractStorageCodec.Decode(payload));

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
