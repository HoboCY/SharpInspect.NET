using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Plc;

namespace SharpInspect.SampleHost;

/// <summary>Public package consumer and human-readable byte preview; never opens a PLC connection.</summary>
internal static class PlcResultPayloadDemo
{
    internal static int Run(string directory)
    {
        try
        {
            RunAsync(Path.GetFullPath(directory)).GetAwaiter().GetResult();
            Console.WriteLine("V131-P01 plc-result-payload PASS publicInputs=true goldenBytes=true wholeFault=true productionReady=false");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"V131-P01 plc-result-payload FAIL reason={exception.GetType().Name}");
            return 1;
        }
    }

    private static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        if (File.Exists(Path.Combine(directory, "evidence.json"))) throw new InvalidOperationException("EvidenceAlreadyExists");
        var factory = new PixelFactory();
        var schema = factory.Descriptor.ResultSchema;
        var u16 = Wire(PlcWireRepresentation.UInt16);
        var u32 = Wire(PlcWireRepresentation.UInt32);
        var contract = new PlcResultContract("Sample.PlcResult", "1", 100, 100, new[]
        {
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ControllerEpoch, new(10, 2), u32),
            new(PlcFrameworkResultField.ResultSequence, new(12, 2), u32),
            new(PlcFrameworkResultField.ExecutionStatus, new(14, 1), u16, executionStatusCodes: new[]
            { new PlcExecutionStatusCode(ExecutionStatus.Success, 9), new(ExecutionStatus.Error, 10), new(ExecutionStatus.Timeout, 11), new(ExecutionStatus.Cancelled, 12) }),
            new(PlcFrameworkResultField.InspectionDecision, new(15, 1), u16, inspectionDecisionCodes: new[]
            { new PlcInspectionDecisionCode(InspectionDecision.Pass, 1), new(InspectionDecision.Fail, 2), new(InspectionDecision.Unknown, 3) }),
            new(PlcFrameworkResultField.ResultReasonCode, new(16, 1), u16, reasonCodes: new[]
            { new PlcReasonCode(null, 0), new("Dark", 20), new("Uncertain", 21), new("AlgorithmHung", 30),
                new("AlgorithmExecutionTimeout", 31), new("AlgorithmExecutionCancelled", 32), new("AlgorithmExecutionError", 33), new("AlgorithmResultContractViolation", 34) })
        }, new[]
        {
            new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash), new[]
            {
                new PlcMeasurementMapping("Length", new PlcRegisterRange(20, 1), new("mm", "hundredth_mm", new(100), new(0)),
                    Wire(PlcWireRepresentation.Int16, PlcRoundingMode.ToNearestTiesToEven), Literal("8000")),
                new("Pixel", new PlcRegisterRange(25, 1), new("count", "count", new(1), new(0)), u16, Literal("EEEE")),
                new("Quality", new PlcRegisterRange(30, 1), new("count", "count", new(1), new(0)), u16, Literal("EEEE"), new PlcOptionalAbsence(Literal("FFFF")))
            }, new[] { new PlcConstantField("Marker", new(40, 1), u16, Literal("A55A")) })
        });
        var recipe = new RecipeReference("Sample.PlcResult.Recipe", "1", new string('A', 64));
        var bound = new PlcResultContractBinder().Bind(recipe, factory.Descriptor.Identity, schema, contract);
        Require(bound.Bound && bound.Binding is not null, bound.ReasonCode);
        var binding = bound.Binding!;
        var configuration = AlgorithmConfigurationSnapshot.Create(factory.Descriptor.ConfigurationSchema, Array.Empty<AlgorithmConfigurationEntry>());
        await using var preparation = new AlgorithmPreparationService(new[] { factory }, new(TimeSpan.FromSeconds(5)));
        var prepared = await preparation.PrepareAsync(new(factory.Descriptor.Identity, configuration, schema.Id, schema.Version,
            schema.ContentHash, schema.OverlayContract.Id, schema.OverlayContract.Version, schema.OverlayContract.ContentHash, TimeSpan.FromSeconds(2)));
        Require(prepared.Succeeded && prepared.Prepared is not null, prepared.ReasonCode);
        await using var instance = prepared.Prepared!;
        await using var execution = new AlgorithmExecutionService(new(new("Sample.PlcResult.Execution", "1", TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(1)));
        using var pool = new FrameBufferPool(new(1, 8, TimeSpan.FromSeconds(1)));
        await using var runtime = new StationRuntime();
        var before = await runtime.GetSnapshotAsync();
        var payloads = new List<PlcResultPayloadPreview>();
        foreach (var marker in new byte[] { 255, 0, 127, 254 })
        {
            var input = Frame(marker);
            var frame = pool.TryCopyFrame(input.Metadata, input.Provenance, new[] { marker, marker });
            Require(frame.Succeeded && frame.Lease is not null, "FrameUnavailable");
            var attempt = await execution.ExecuteAsync(instance, frame.Lease!, new(recipe, TimeSpan.FromSeconds(1)));
            Require(attempt.Executed && attempt.Outcome is not null, attempt.ReasonCode);
            var drainedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (pool.GetSnapshot().OutstandingLeases != 0 && DateTime.UtcNow < drainedDeadline) await Task.Delay(1);
            Require(pool.GetSnapshot().OutstandingLeases == 0, "FrameLeaseNotReturned");
            var encoded = new PlcResultPayloadEncoder().EncodePreview(binding, 0x11223344, 0x55667788, attempt.Outcome!);
            Require(encoded.Succeeded && encoded.Preview is not null, encoded.DetailReasonCode ?? encoded.ReasonCode);
            payloads.Add(encoded.Preview!);
            var wrong = new PlcResultContractBinder().Bind(new("Different.Recipe", "1", new string('B', 64)), factory.Descriptor.Identity, schema, contract);
            var fault = new PlcResultPayloadEncoder().EncodePreview(wrong.Binding!, 1, 2, attempt.Outcome!);
            Require(!fault.Succeeded && fault.Preview is null && fault.ReasonCode == "PlcResultEncodingFault", "WholeFaultRequired");
            var production = new PlcResultPayloadEncoder().Encode(binding, new(1, 2), attempt.Outcome!);
            Require(!production.Succeeded && production.Snapshot is null &&
                production.DetailReasonCode == "PlcResultProductionCorrelationRequired", "ManualResultReachedProductionEncoder");
        }
        // Fixed independent expected bytes for marker 255. Other markers check decision/absence/error axes below.
        var expected = new[] { "10:11223344", "12:55667788", "14:0009", "15:0001", "16:0000", "20:18E7", "25:00FF", "30:0064", "40:A55A" };
        Require(Lines(payloads[0]).SequenceEqual(expected), "IndependentGoldenBytesMismatch");
        Require(payloads[0].WireContentHash == "3054A798014024E781694697959F4AD803D1371D085E5D6479BB8309301DE3C4", "IndependentWireHashMismatch");
        Require(Hex(payloads[1], 15) == "0002" && Hex(payloads[1], 16) == "0014" && Hex(payloads[1], 30) == "FFFF", "AbsentValueMismatch");
        Require(Hex(payloads[2], 14) == "0009" && Hex(payloads[2], 15) == "0003" && Hex(payloads[2], 16) == "0015", "UnknownDecisionMismatch");
        Require(Hex(payloads[3], 14) == "000A" && Hex(payloads[3], 15) == "0003" && Hex(payloads[3], 16) == "0021" &&
            Hex(payloads[3], 20) == "8000" && Hex(payloads[3], 25) == "EEEE" && Hex(payloads[3], 30) == "EEEE", "ErrorPayloadMismatch");
        var after = await runtime.GetSnapshotAsync();
        Require(!before.Ready && !after.Ready && after.ArmState == ProductionArmState.Disarmed && pool.GetSnapshot().OutstandingLeases == 0,
            "DevelopmentBoundaryChanged");
        var assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PlcResultPayloadDemo).Assembly.Location)));
        var evidence = new
        {
            ValidationId = "V131-P01", Result = "Pass", ConsumerSha256 = assemblyHash, PublicAlgorithmInput = true,
            PayloadPreview = true, ProductionPayloadSnapshot = false, ProductionExecutionAdmission = "NotRun",
            SourcePixels = new[] { 255, 0, 127, 254 }, IndependentGoldenBytes = expected, ActualGoldenBytes = Lines(payloads[0]),
            WholeFault = true, Ready = after.Ready, ArmState = after.ArmState.ToString(), OutstandingLeases = pool.GetSnapshot().OutstandingLeases,
            Contract = contract.Reference, Schema = new RecipeContractReference(schema.Id, schema.Version, schema.ContentHash), BindingHash = binding.ContentHash,
            Checks = binding.Validation.Checks.Select(value => new { value.ValidationId, value.Subject, value.Passed, value.ReasonCode, value.ContentHash }),
            Payloads = payloads.Select(value => new { value.SourceExecution, value.ExecutionStatus, value.Decision, value.ReasonCode,
                value.SampleControllerEpoch, value.SampleCycleSequence, value.PayloadBytes, value.WireContentHash, value.ContentHash, Segments = Lines(value) }),
            HardwarePlc = "NotRun", ResultPublicationGate = "NotRun", ResultValid = "NotRun", StationAcceptance = "NotRun"
        };
        File.WriteAllText(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "payload-preview.html"), Report(binding, payloads), new UTF8Encoding(false));
    }

    private static string Report(PlcResultContractBinding binding, IEnumerable<PlcResultPayloadPreview> snapshots)
    {
        static string Esc(string? text) => WebUtility.HtmlEncode(text ?? "none");
        var html = new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><title>PLC 结果契约与字节预览</title><style>body{font:16px system-ui;max-width:1100px;margin:32px auto;color:#192532}table{border-collapse:collapse;width:100%}td,th{padding:8px;border:1px solid #b8c6d2;text-align:left}code{overflow-wrap:anywhere}section{margin:32px 0}</style><h1>PLC 结果契约与字节预览</h1>");
        html.Append($"<p>契约 {Esc(binding.Contract.Id)} / {Esc(binding.Contract.Version)}<br><code>{Esc(binding.Contract.ContentHash)}</code></p>");
        html.Append("<p>地址单位为 16 位寄存器，十六进制数为按地址递增排列的最终 PDU 字节。稀疏地址之间的空隙没有写入值。Length：mm × 100 → hundredth_mm，舍入为最近值、偶数优先。</p>");
        foreach (var snapshot in snapshots)
        {
            html.Append($"<section><h2>{snapshot.ExecutionStatus} / {snapshot.Decision}</h2><p>原因：{Esc(snapshot.ReasonCode)}<br>报文字节哈希：<code>{snapshot.WireContentHash}</code><br>完整快照哈希：<code>{snapshot.ContentHash}</code></p><table><tr><th>寄存器起始地址</th><th>寄存器数量</th><th>十六进制字节</th></tr>");
            foreach (var segment in snapshot.Segments)
                html.Append($"<tr><td>{segment.StartRegister}</td><td>{segment.RegisterCount}</td><td><code>{Convert.ToHexString(segment.RegisterBytes.ToArray())}</code></td></tr>");
            html.Append("</table></section>");
        }
        html.Append("<p>这是实际 Manual 运算的字节预览，控制器值为示例输入，没有生产 InspectionId 或发布快照。未连接 PLC；尚未执行生产准入、持久发布门禁、ResultValid 写入或真实工位验收。</p></html>");
        return html.ToString();
    }

    private static (FrameMetadata Metadata, FrameProvenance Provenance) Frame(byte marker)
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.Parse($"13100000-0000-0000-0000-{marker:D12}"));
        var effective = new EffectiveCameraConfiguration(ProductionAcquisitionMode.HardwareTrigger, 500, 1, new(0, 0, 2, 1), VisionPixelFormat.Mono8, null, 500, 0, null);
        var utc = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var metadata = new FrameMetadata(correlation, "Sample.PlcResult.Camera", 2, 1, 2, VisionPixelFormat.Mono8, null, utc, effective);
        var milestones = new FrameAcquisitionMilestones(1000000, new(utc, 10), new(utc, 12), new(utc, 14), new(utc, 16));
        return (metadata, new(correlation, "virtual", "1", "adapter", "1", "sdk", "1", null, "fixture", null, null,
            "Mono8", "normalized-v1", false, false, null, null, milestones));
    }
    private static PlcWireEncoding Wire(PlcWireRepresentation representation, PlcRoundingMode rounding = PlcRoundingMode.Exact) => new(representation,
        PlcByteOrder.BigEndian, representation is PlcWireRepresentation.UInt16 or PlcWireRepresentation.Int16 ? PlcWordOrder.NotApplicable : PlcWordOrder.HighWordFirst,
        rounding, PlcOverflowBehavior.EncodingFault);
    private static PlcWireLiteral Literal(string value) => new(Convert.FromHexString(value));
    private static string[] Lines(PlcResultPayloadPreview snapshot) => snapshot.Segments.Select(value => $"{value.StartRegister}:{Convert.ToHexString(value.RegisterBytes.ToArray())}").ToArray();
    private static string Hex(PlcResultPayloadPreview snapshot, int address) => Convert.ToHexString(snapshot.Segments.Single(value => value.StartRegister == address).RegisterBytes.ToArray());
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

    private sealed class PixelFactory : IVisionAlgorithmFactory
    {
        public AlgorithmDescriptor Descriptor { get; } = new(new("Sample.PlcResult.PixelAlgorithm", "1"),
            new("Sample.PlcResult.Config", "1", Array.Empty<AlgorithmFieldDefinition>()),
            new("Sample.PlcResult.Result", "1", new[]
            {
                new AlgorithmFieldDefinition("Length", AlgorithmScalarType.Float64, "mm", true, new(minFloat64: 0, maxFloat64: 64)),
                new AlgorithmFieldDefinition("Pixel", AlgorithmScalarType.Int64, "count", true, new(minInt64: 0, maxInt64: 255)),
                new AlgorithmFieldDefinition("Quality", AlgorithmScalarType.Int64, "count", false, new(minInt64: 0, maxInt64: 100))
            }, new[] { "Dark", "Uncertain" }, new("Sample.PlcResult.Overlay", "1")));
        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>());
        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IVisionAlgorithm>(new PixelAlgorithm(Descriptor.ResultSchema.OverlayContract));
    }
    private sealed class PixelAlgorithm : IVisionAlgorithm
    {
        private readonly OverlayContract _overlay;
        internal PixelAlgorithm(OverlayContract overlay) => _overlay = overlay;
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context, CancellationToken cancellationToken = default)
        {
            var pixel = context.Frame.GetRowSpan(0)[0];
            if (pixel == 254) throw new InvalidOperationException("fixture error");
            var measurements = new List<AlgorithmMeasurement>
            { new("Length", "mm", AlgorithmScalarValue.FromFloat64(pixel / 4.0)), new("Pixel", "count", AlgorithmScalarValue.FromInt64(pixel)) };
            if (pixel != 0) measurements.Add(new("Quality", "count", AlgorithmScalarValue.FromInt64(100)));
            return ValueTask.FromResult(new AlgorithmResult(pixel == 0 ? InspectionDecision.Fail : pixel == 127 ? InspectionDecision.Unknown : InspectionDecision.Pass,
                pixel == 0 ? "Dark" : pixel == 127 ? "Uncertain" : null, measurements, new OutputOverlaySet(_overlay)));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
