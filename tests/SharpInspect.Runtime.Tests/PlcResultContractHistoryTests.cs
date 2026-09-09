using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>Focused replay checks for release snapshots referenced by schema17 contract history.</summary>
public sealed class PlcResultContractHistoryTests
{
    [Fact]
    public void V131_H01_LowerHighwatermarkCannotSkipAReleaseAlreadyBeforeContractAudit()
    {
        var schema = Schema("A");
        var contract = Contract(schema, "1");
        var release = Release(1, "A", schema);
        var revision = ContractRevision(1, contract, schema, release, 0, null, includeBinding: false);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqliteCommandStore.ValidatePlcResultContractReleaseBindings(
                new[] { new PlcResultContractHistoryFact(revision, 10) },
                new[] { new PlcResultContractReleaseHistoryFact(release, 5) }));

        Assert.Equal("PlcResultContractReleaseSnapshotInvalid", exception.Message);
    }

    [Fact]
    public void V131_H02_RemovingAnOldSchemaStillUsedByTheReleaseSnapshotIsRejected()
    {
        var oldSchema = Schema("A");
        var newSchema = Schema("B");
        var oldContract = Contract(oldSchema, "1");
        var newContract = Contract(newSchema, "2");
        var oldRelease = Release(1, "A", oldSchema);
        var newRelease = Release(2, "B", newSchema);
        var oldRevision = ContractRevision(1, oldContract, oldSchema, oldRelease, 1, null);
        var newRevision = ContractRevision(2, newContract, newSchema, newRelease, 2,
            oldRevision.Reference);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqliteCommandStore.ValidatePlcResultContractReleaseBindings(
                new[]
                {
                    new PlcResultContractHistoryFact(oldRevision, 10),
                    new PlcResultContractHistoryFact(newRevision, 20)
                },
                new[]
                {
                    new PlcResultContractReleaseHistoryFact(oldRelease, 5),
                    new PlcResultContractReleaseHistoryFact(newRelease, 15)
                }));

        Assert.Equal("PlcResultContractReleaseCoverageMismatch", exception.Message);
    }

    [Fact]
    public void V131_H03_LaterReleaseDoesNotInvalidateAnEarlierContractSnapshot()
    {
        var oldSchema = Schema("A");
        var laterSchema = Schema("B");
        var contract = Contract(oldSchema, "1");
        var oldRelease = Release(1, "A", oldSchema);
        var laterRelease = Release(2, "B", laterSchema);
        var revision = ContractRevision(1, contract, oldSchema, oldRelease, 1, null);

        SqliteCommandStore.ValidatePlcResultContractReleaseBindings(
            new[] { new PlcResultContractHistoryFact(revision, 10) },
            new[]
            {
                new PlcResultContractReleaseHistoryFact(oldRelease, 5),
                new PlcResultContractReleaseHistoryFact(laterRelease, 15)
            });
    }

    [Fact]
    public void V131_H04_UnrelatedSchemaInTheSameReleaseSnapshotIsIgnored()
    {
        var currentSchema = Schema("A");
        var unrelatedSchema = Schema("B");
        var contract = Contract(currentSchema, "1");
        var currentRelease = Release(1, "A", currentSchema);
        var unrelatedRelease = Release(2, "B", unrelatedSchema);
        var revision = ContractRevision(1, contract, currentSchema, currentRelease, 2, null);

        SqliteCommandStore.ValidatePlcResultContractReleaseBindings(
            new[] { new PlcResultContractHistoryFact(revision, 10) },
            new[]
            {
                new PlcResultContractReleaseHistoryFact(currentRelease, 5),
                new PlcResultContractReleaseHistoryFact(unrelatedRelease, 6)
            });
    }

    [Fact]
    public void V131_H05_RemovedUnusedSchemaDoesNotPoisonALaterUnrelatedContractRevision()
    {
        var retainedSchema = Schema("A");
        var removedUnusedSchema = Schema("B");
        var laterSchema = Schema("C");
        var initialContract = Contract(new[] { retainedSchema, removedUnusedSchema }, "1");
        var retainedContract = Contract(retainedSchema, "2");
        var laterContract = Contract(laterSchema, "3");
        var initialRevision = ContractRevisionWithoutBindings(1, initialContract,
            new[] { retainedSchema, removedUnusedSchema }, 0, null);
        var retainedRevision = ContractRevisionWithoutBindings(2, retainedContract,
            new[] { retainedSchema }, 0, initialRevision.Reference);
        var removedRelease = Release(1, "B", removedUnusedSchema);
        var laterRelease = Release(2, "C", laterSchema);
        var laterRevision = ContractRevision(3, laterContract, laterSchema, laterRelease, 2,
            retainedRevision.Reference);

        SqliteCommandStore.ValidatePlcResultContractReleaseBindings(
            new[]
            {
                new PlcResultContractHistoryFact(initialRevision, 10),
                new PlcResultContractHistoryFact(retainedRevision, 20),
                new PlcResultContractHistoryFact(laterRevision, 40)
            },
            new[]
            {
                new PlcResultContractReleaseHistoryFact(removedRelease, 25),
                new PlcResultContractReleaseHistoryFact(laterRelease, 35)
            });
    }

    private static PlcResultContractRevision ContractRevision(long position,
        PlcResultContract contract, AlgorithmResultSchema schema, RecipeReleaseRecord release,
        long releaseHighWatermark, RecipeContractReference? previous,
        bool includeBinding = true)
    {
        var binding = Bind(contract, schema, release);
        var releasedBinding = includeBinding
            ? new[] { new PlcReleasedRecipeBinding(release.ReleaseId, release.ContentHash, binding) }
            : Array.Empty<PlcReleasedRecipeBinding>();
        var reason = $"history contract revision {position}";
        return new PlcResultContractRevision(position, GuidFor(300 + (int)position),
            GuidFor(400 + (int)position), contract, previous, releaseHighWatermark,
            new[] { binding.Validation }, releasedBinding,
            GuidFor(500 + (int)position), GuidFor(600 + (int)position), position,
            GuidFor(700 + (int)position), new RecipeContractReference("History.Authorization", "1",
                Hash("history-policy")), reason,
            ChangePlcResultContractCommand.ComputeAuthorizationTarget(contract, previous, reason),
            DateTimeOffset.Parse("2026-09-09T00:00:00Z").AddMinutes(position));
    }

    private static PlcResultContractRevision ContractRevisionWithoutBindings(long position,
        PlcResultContract contract, IReadOnlyList<AlgorithmResultSchema> schemas,
        long releaseHighWatermark, RecipeContractReference? previous)
    {
        var validations = schemas.Select(schema => Bind(contract, schema, null).Validation).ToArray();
        var reason = $"history contract revision {position}";
        return new PlcResultContractRevision(position, GuidFor(300 + (int)position),
            GuidFor(400 + (int)position), contract, previous, releaseHighWatermark,
            validations, Array.Empty<PlcReleasedRecipeBinding>(),
            GuidFor(500 + (int)position), GuidFor(600 + (int)position), position,
            GuidFor(700 + (int)position), new RecipeContractReference("History.Authorization", "1",
                Hash("history-policy")), reason,
            ChangePlcResultContractCommand.ComputeAuthorizationTarget(contract, previous, reason),
            DateTimeOffset.Parse("2026-09-09T00:00:00Z").AddMinutes(position));
    }

    private static PlcResultContractBinding Bind(PlcResultContract contract,
        AlgorithmResultSchema schema, RecipeReleaseRecord? release)
    {
        var recipe = release?.Recipe ?? new RecipeReference(
            "History.SchemaOnly.Recipe." + schema.Id, "1", Hash("schema-only-recipe-" + schema.Id));
        var algorithm = release?.Source.Content.Algorithm.Algorithm ??
            new AlgorithmIdentity("History.SchemaOnly.Algorithm." + schema.Id, "1");
        var result = new PlcResultContractBinder().Bind(recipe, algorithm, schema, contract);
        Assert.True(result.Bound,
            string.Join(",", result.Checks.Where(value => !value.Passed).Select(value => value.ReasonCode)));
        return Assert.IsType<PlcResultContractBinding>(result.Binding);
    }

    private static PlcResultContract Contract(AlgorithmResultSchema schema, string version)
        => Contract(new[] { schema }, version);

    private static PlcResultContract Contract(IReadOnlyList<AlgorithmResultSchema> schemas, string version)
    {
        return new PlcResultContract("History.PlcContract", version, 100, 100,
            PlcResultPayloadTests.FrameworkFields(), schemas.Select(schema => new PlcResultSchemaMap(
                new RecipeContractReference(schema.Id, schema.Version, schema.ContentHash),
                PlcResultPayloadTests.Mappings(), new[]
                {
                    new PlcConstantField("Marker", new(40, 1), PlcResultPayloadTests.U16,
                        PlcResultPayloadTests.Literal("A55A"))
                })).ToArray());
    }

    private static AlgorithmResultSchema Schema(string suffix) => new(
        "History.Result." + suffix, "1", new[]
        {
            new AlgorithmFieldDefinition("Length", AlgorithmScalarType.Float64, "mm", true,
                new AlgorithmScalarConstraints(minFloat64: -3, maxFloat64: 3)),
            new AlgorithmFieldDefinition("Score", AlgorithmScalarType.Int64, "count", false,
                new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100)),
            new AlgorithmFieldDefinition("Label", AlgorithmScalarType.Enum, "state", true,
                new AlgorithmScalarConstraints(allowedValues: new[] { "Good", "Bad" })),
            new AlgorithmFieldDefinition("Note", AlgorithmScalarType.String, "text", false)
        }, new[] { "Defect", "Uncertain" },
        new OverlayContract("History.Overlay." + suffix, "1"));

    private static RecipeReleaseRecord Release(long position, string suffix,
        AlgorithmResultSchema schema)
    {
        var author = GuidFor(1000 + (int)position);
        var source = Source(position, suffix, schema, author);
        var policy = new RecipeGovernancePolicy("History.Release", "1",
            RecipeGovernanceMode.SingleApproverRelease);
        var invocation = GuidFor(1100 + (int)position);
        return new RecipeReleaseRecord(position, GuidFor(1200 + (int)position),
            GuidFor(1300 + (int)position), 1, source, policy,
            new[] { new RecipeReleaseValidationCheck("Structure", "history", true, "Passed") },
            new[] { new RecipeReleaseChange("Configuration/value", null, suffix, author,
                source.DraftId, source.Revision) }, author, GuidFor(1400 + (int)position), 1,
            GuidFor(1500 + (int)position), new RecipeContractReference("History.Authorization", "1",
                Hash("history-authorization")), "history release " + suffix, Hash("release-target-" + suffix),
            DateTimeOffset.Parse("2026-09-09T00:00:00Z").AddSeconds(position));
    }

    private static RecipeDraftRevision Source(long position, string suffix,
        AlgorithmResultSchema schema, Guid author)
    {
        var configurationSchema = new AlgorithmConfigurationSchema("History.Config." + suffix, "1",
            Array.Empty<AlgorithmFieldDefinition>());
        var resultReference = new RecipeContractReference(schema.Id, schema.Version, schema.ContentHash);
        var overlayReference = new RecipeContractReference(schema.OverlayContract.Id,
            schema.OverlayContract.Version, schema.OverlayContract.ContentHash);
        var content = new RecipeDraftContent("History.Recipe." + suffix, "history recipe",
            new RecipeAlgorithmBinding(new AlgorithmIdentity("History.Algorithm." + suffix, "1"),
                configurationSchema, resultReference, overlayReference),
            AlgorithmConfigurationSnapshot.Create(configurationSchema, Array.Empty<AlgorithmConfigurationEntry>()),
            "TopCamera", new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                100, 0, new(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 500, 0, null),
            TimeSpan.FromMilliseconds(100), null, null);
        return new RecipeDraftRevision(position, GuidFor(1600 + (int)position), 1,
            GuidFor(1700 + (int)position), null, Hash("history-source-" + suffix), content,
            author, GuidFor(1800 + (int)position), 1, "history source",
            DateTimeOffset.Parse("2026-09-09T00:00:00Z").AddSeconds(position));
    }

    private static Guid GuidFor(int value) => Guid.Parse(
        $"13100000-0000-0000-0000-{value:000000000000}");

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
