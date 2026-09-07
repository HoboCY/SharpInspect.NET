using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

/// <summary>
/// A bounded development consumer for the result archive and read-only overlay
/// viewer. It intentionally runs before the normal WPF application is created and
/// never enters the station's production trigger or Ready path.
/// </summary>
internal static class AlgorithmOverlayDemo
{
    private const string StationId = "SampleOverlayStation";
    private const string AuditKeyName = "SharpInspect.SampleOverlay";
    private const string DatabaseName = "overlay.sqlite";
    private const int Width = 64;
    private const int Height = 48;

    public static int Run(string directory)
    {
        try
        {
            _ = RunCore(Path.GetFullPath(directory));
            Console.WriteLine("V114-N01 overlay-consumer PASS primitives=10 empty=true " +
                "maliciousRejected=true geometryUnchanged=true sourceSeparated=true productionReady=false");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("V114-N01 overlay-consumer FAIL reason=AlgorithmOverlayConsumerCheckFailed");
            return 1;
        }
    }

    public static int Query(string directory)
    {
        try
        {
            var root = Path.GetFullPath(directory);
            var records = QueryRecords(root);
            Require(VerifyRestartEvidence(root, records), "AlgorithmOverlayRestartEvidenceMismatch");
            WriteRestartEvidence(root, records.Count);
            Console.WriteLine("V114-N02 overlay-restart PASS records=2 originalSchema=true sourceImageRequired=false");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("V114-N02 overlay-restart FAIL reason=AlgorithmOverlayQueryCheckFailed");
            return 1;
        }
    }

    private static OverlayEvidence RunCore(string directory)
    {
        Directory.CreateDirectory(directory);
        if (File.Exists(Path.Combine(directory, DatabaseName)))
            throw new InvalidOperationException("AlgorithmOverlayDatabaseAlreadyExists");
        var options = CreateOptions(directory);
        var factory = new OverlayFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IVisionAlgorithmFactory>(factory);
        services.AddSharpInspectAlgorithmPreparation(new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5)));
        services.AddSharpInspectFrameBufferPool(new FrameBufferPoolOptions(1, 64 * 1024,
            TimeSpan.FromMilliseconds(100)));
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));

        var provider = services.BuildServiceProvider();
        AlgorithmExecutionService? execution = null;
        PreparedAlgorithm? prepared = null;
        FramePreviewImage? sourceImage = null;
        AlgorithmExecutionOutcome? passOutcome = null;
        AlgorithmExecutionOutcome? emptyOutcome = null;
        try
        {
            var runtime = provider.GetRequiredService<IStationRuntime>();
            WaitForVerified(runtime);

            var preparer = provider.GetRequiredService<AlgorithmPreparationService>();
            var configuration = AlgorithmConfigurationSnapshot.Create(
                factory.Descriptor.ConfigurationSchema, Array.Empty<AlgorithmConfigurationEntry>());
            var preparedResult = preparer.PrepareAsync(CreatePreparationRequest(factory.Descriptor, configuration))
                .GetAwaiter().GetResult();
            Require(preparedResult.Succeeded && preparedResult.Prepared is not null,
                "AlgorithmPreparationFailed");
            prepared = preparedResult.Prepared!;

            execution = new AlgorithmExecutionService(new AlgorithmExecutionOptions(
                new AlgorithmExecutionPolicy("Sample.OverlayExecution", "1",
                    TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)),
                TimeSpan.FromSeconds(2)));
            var pool = provider.GetRequiredService<FrameBufferPool>();

            var pass = ExecuteMarker(10, pool, execution, prepared, capturePreview: true);
            Require(pass.Attempt.Executed && pass.Attempt.Outcome is not null &&
                pass.Attempt.Outcome.ExecutionStatus == ExecutionStatus.Success &&
                pass.Attempt.Outcome.ValidatedResult is not null &&
                pass.Attempt.Outcome.ValidatedResult.OverlaySet.Primitives.Count == 10,
                "AlgorithmOverlayTenPrimitiveExecutionFailed");
            passOutcome = pass.Attempt.Outcome!;
            sourceImage = pass.Preview;
            Require(sourceImage is not null, "AlgorithmOverlaySourcePreviewMissing");

            var empty = ExecuteMarker(20, pool, execution, prepared, capturePreview: false);
            Require(empty.Attempt.Executed && empty.Attempt.Outcome is not null &&
                empty.Attempt.Outcome.ExecutionStatus == ExecutionStatus.Success &&
                empty.Attempt.Outcome.ValidatedResult is not null &&
                empty.Attempt.Outcome.ValidatedResult.OverlaySet.Primitives.Count == 0,
                "AlgorithmOverlayEmptyExecutionFailed");
            emptyOutcome = empty.Attempt.Outcome!;

            var invalid = ExecuteMarker(30, pool, execution, prepared, capturePreview: false);
            Require(invalid.Attempt.Executed && invalid.Attempt.Outcome is not null &&
                invalid.Attempt.Outcome.ExecutionStatus == ExecutionStatus.Error &&
                invalid.Attempt.Outcome.ValidatedResult is null &&
                invalid.Attempt.Outcome.ReasonCode == "AlgorithmResultContractViolation",
                "AlgorithmOverlayInvalidResultWasAccepted");

            var archive = provider.GetRequiredService<AlgorithmResultArchive>();
            var passRecordId = Guid.NewGuid();
            var emptyRecordId = Guid.NewGuid();
            var passWrite = archive.AppendAsync(passRecordId, passOutcome).GetAwaiter().GetResult();
            Require(passWrite.Recorded && passWrite.RecordId == passRecordId, "AlgorithmOverlayArchivePassFailed");
            WaitForVerified(runtime);
            var emptyWrite = archive.AppendAsync(emptyRecordId, emptyOutcome).GetAwaiter().GetResult();
            Require(emptyWrite.Recorded && emptyWrite.RecordId == emptyRecordId, "AlgorithmOverlayArchiveEmptyFailed");
            WaitForVerified(runtime);
        }
        finally
        {
            if (prepared is not null)
                prepared.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (execution is not null)
                execution.DisposeAsync().AsTask().GetAwaiter().GetResult();
            DisposeProvider(provider);
        }

        Require(sourceImage is not null && passOutcome is not null, "AlgorithmOverlayEvidenceMissing");
        var records = QueryRecords(directory);
        var passRecord = records.Single(record => record.Correlation == passOutcome!.Correlation);
        var emptyRecord = records.Single(record => record.Correlation == emptyOutcome!.Correlation);
        Require(passRecord.Result.OverlaySet.Primitives.Count == 10 &&
            emptyRecord.Result.OverlaySet.Primitives.Count == 0,
            "AlgorithmOverlayQueryContentInvalid");

        var sourcePath = Path.Combine(directory, "source-frame.png");
        var derivedPath = Path.Combine(directory, "overlay-preview.png");
        var viewerPath = Path.Combine(directory, "overlay-viewer.png");
        var source = sourceImage!;
        SavePng(source.BitmapSource, sourcePath);
        var rendered = RenderPreview(passRecord, source);
        var sourceSeparated = source.SourcePreviewId == rendered.SourcePreviewId &&
            rendered.SourcePixelHash == source.SourcePixelHash &&
            rendered.OverlaySetId == passRecord.RecordId &&
            rendered.OverlaySetId != source.SourcePreviewId &&
            rendered.RendererContractId == OverlayRenderingContract.Id &&
            rendered.RendererContractVersion == OverlayRenderingContract.Version &&
            rendered.RendererContractContentHash == OverlayRenderingContract.ContentHash;
        var geometryUnchanged = rendered.OverlayContentHash == passRecord.ContentHash &&
            rendered.OverlaySetId == passRecord.RecordId;
        Require(sourceSeparated, "AlgorithmOverlaySourceIdentityMismatch");
        Require(geometryUnchanged, "AlgorithmOverlayGeometryChanged");
        SavePng(rendered.BitmapSource, derivedPath);
        RenderViewer(directory, source, viewerPath);

        var reloaded = QueryRecords(directory);
        Require(reloaded.Count == records.Count &&
            reloaded.Any(record => record.RecordId == passRecord.RecordId),
            "AlgorithmOverlayRestartQueryFailed");
        var reloadedPass = reloaded.Single(record => record.RecordId == passRecord.RecordId);
        Require(string.Equals(passRecord.ContentHash, reloadedPass.ContentHash, StringComparison.Ordinal),
            "AlgorithmOverlayPayloadHashChangedAfterRestart");

        var evidence = new OverlayEvidence(
            records.Count,
            passRecord.Result.OverlaySet.Primitives.Count,
            reloaded.Count,
            sourceSeparated,
            geometryUnchanged && string.Equals(passRecord.ContentHash, reloadedPass.ContentHash, StringComparison.Ordinal),
            passRecord.ContentHash, source.SourcePixelHash,
            passRecord.RecordId.ToString("D"), passRecord.ResultSchema.Id,
            passRecord.ResultSchema.Version, passRecord.ResultSchema.ContentHash,
            passRecord.ResultSchema.OverlayContract.Id,
            passRecord.ResultSchema.OverlayContract.Version,
            passRecord.ResultSchema.OverlayContract.ContentHash,
            Path.GetFileName(sourcePath), Path.GetFileName(derivedPath), Path.GetFileName(viewerPath));
        File.WriteAllText(Path.Combine(directory, "overlay-evidence.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        Require(VerifyRestartEvidence(directory, reloaded), "AlgorithmOverlayRestartEvidenceMismatch");
        WriteRestartEvidence(directory, reloaded.Count);
        return evidence;
    }

    private static void WriteRestartEvidence(string directory, int recordCount)
    {
        var evidence = new
        {
            Marker = "V114-N02",
            Records = recordCount,
            OriginalSchema = true,
            SourceImageRequired = false,
            AlgorithmFactoryRegistered = false
        };
        File.WriteAllText(Path.Combine(directory, "overlay-restart.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static bool VerifyRestartEvidence(string directory,
        IReadOnlyList<AlgorithmResultRecord> records)
    {
        var path = Path.Combine(directory, "overlay-evidence.json");
        if (!File.Exists(path)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (records.Count != 2 || root.GetProperty("RecordCount").GetInt32() != 2)
            return false;
        var recordId = Guid.Parse(RequiredText(root, "PassRecordId"));
        var expected = records.SingleOrDefault(record => record.RecordId == recordId);
        if (expected is null) return false;
        if (root.GetProperty("RecordCount").GetInt32() != records.Count ||
            root.GetProperty("PrimitiveCount").GetInt32() != expected.Result.OverlaySet.Primitives.Count)
            return false;
        if (!string.Equals(RequiredText(root, "PayloadHash"), expected.ContentHash, StringComparison.Ordinal) ||
            !string.Equals(RequiredText(root, "ResultSchemaId"), expected.ResultSchema.Id, StringComparison.Ordinal) ||
            !string.Equals(RequiredText(root, "ResultSchemaVersion"), expected.ResultSchema.Version, StringComparison.Ordinal) ||
            !string.Equals(RequiredText(root, "ResultSchemaContentHash"), expected.ResultSchema.ContentHash, StringComparison.Ordinal))
            return false;
        var contract = expected.ResultSchema.OverlayContract;
        return string.Equals(RequiredText(root, "OverlayContractId"), contract.Id, StringComparison.Ordinal) &&
            string.Equals(RequiredText(root, "OverlayContractVersion"), contract.Version, StringComparison.Ordinal) &&
            string.Equals(RequiredText(root, "OverlayContractContentHash"), contract.ContentHash, StringComparison.Ordinal) &&
            root.GetProperty("PayloadStable").GetBoolean() &&
            root.GetProperty("SourceDerivedDistinct").GetBoolean();
    }

    private static string RequiredText(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).GetString() ?? throw new InvalidOperationException("OverlayEvidenceTextMissing");

    private static List<AlgorithmResultRecord> QueryRecords(string directory)
    {
        var provider = BuildReadProvider(directory);
        try
        {
            var query = provider.GetRequiredService<IAlgorithmResultQuery>();
            var page = query.QueryAsync(new AlgorithmResultFilter(PageSize: 10))
                .GetAwaiter().GetResult();
            Require(page.Available, page.ReasonCode);
            return page.Records.ToList();
        }
        finally
        {
            DisposeProvider(provider);
        }
    }

    private static IServiceProvider BuildReadProvider(string directory)
    {
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(CreateOptions(directory),
            TimeSpan.FromMilliseconds(100));
        return services.BuildServiceProvider();
    }

    private static void RenderViewer(string directory, FramePreviewImage sourceImage, string outputPath)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var provider = BuildReadProvider(directory);
        var query = provider.GetRequiredService<IAlgorithmResultQuery>();
        var viewModel = new AlgorithmResultHistoryViewModel(query,
            new DispatcherUiDispatcher(dispatcher), pageSize: 10);
        var panel = new AlgorithmResultPanel { DataContext = viewModel };
        var window = new Window
        {
            Content = panel,
            Width = 960,
            Height = 760,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -32000,
            Top = -32000
        };
        try
        {
            window.Show();
            PumpDispatcher(dispatcher, async () =>
            {
                await viewModel.RefreshAsync().ConfigureAwait(true);
                Require(viewModel.SelectedRecord is not null, "AlgorithmOverlayViewerSelectionMissing");
                // AlgorithmResultPanel deliberately clears the source on selection
                // changes; bind the source only after the verified selection settles.
                panel.SourceImage = sourceImage;
                panel.Presenter.Zoom = 6;
                panel.Measure(new Size(window.Width, window.Height));
                panel.Arrange(new Rect(0, 0, window.Width, window.Height));
                panel.UpdateLayout();
            });

            Require(panel.Presenter.SourceImageAvailable &&
                panel.Presenter.RenderStatus == FrameOverlayRenderStatus.Available,
                "AlgorithmOverlayViewerSourceUnavailable");
            var width = Math.Max(1, (int)Math.Ceiling(panel.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(panel.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(panel);
            bitmap.Freeze();
            SavePng(bitmap, outputPath);
        }
        finally
        {
            PumpDispatcher(dispatcher, async () => await viewModel.DisposeAsync().ConfigureAwait(true));
            window.Close();
            DisposeProvider(provider);
        }
    }

    private static void PumpDispatcher(Dispatcher dispatcher, Func<Task> operation)
    {
        Exception? failure = null;
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try { await operation().ConfigureAwait(true); }
            catch (Exception exception) { failure = exception; }
            finally { frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static RenderedOverlayPreview RenderPreview(AlgorithmResultRecord record,
        FramePreviewImage sourceImage)
    {
        var presenter = new FrameOverlayPresenter
        {
            Snapshot = record.Overlay,
            SourceImage = sourceImage,
            Zoom = 4
        };
        presenter.Measure(new Size(record.FrameMetadata.Width * 4, record.FrameMetadata.Height * 4));
        presenter.Arrange(new Rect(0, 0, record.FrameMetadata.Width * 4,
            record.FrameMetadata.Height * 4));
        presenter.UpdateLayout();
        return presenter.RenderPreview();
    }

    private static void SavePng(System.Windows.Media.Imaging.BitmapSource bitmap, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private static ExecutedFrame ExecuteMarker(byte marker, FrameBufferPool pool,
        AlgorithmExecutionService execution, PreparedAlgorithm prepared, bool capturePreview)
    {
        var input = CreateInput(marker);
        var copied = pool.TryCopyFrame(input.Metadata, input.Provenance, input.Bytes);
        Require(copied.Succeeded && copied.Lease is not null, copied.ReasonCode);
        var lease = copied.Lease!;
        var frame = lease.Frame;
        FramePreviewImage? preview = capturePreview ? FramePreviewImage.CopyFromFrame(frame) : null;
        try
        {
            var attempt = execution.ExecuteAsync(prepared, lease,
                new AlgorithmExecutionRequest(
                    new RecipeReference("sample-overlay-recipe", "1", new string('A', 64)),
                    TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
            for (var index = 0; index < 100 && frame.IsLoanActive; index++)
                Thread.Sleep(1);
            Require(!frame.IsLoanActive, "AlgorithmOverlayFrameNotReturned");
            return new ExecutedFrame(attempt, preview);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static FrameInput CreateInput(byte marker)
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 0, new RegionOfInterest(0, 0, Width, Height), VisionPixelFormat.Mono8,
            null, 1000, 0, null);
        var metadata = new FrameMetadata(correlation, "Primary", Width, Height, Width,
            VisionPixelFormat.Mono8, null, DateTimeOffset.UtcNow, camera);
        var point = new FrameTimePoint(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        var milestones = new FrameAcquisitionMilestones(Stopwatch.Frequency, point, point, point, point);
        var provenance = new FrameProvenance(correlation, "ManagedFixture", "1",
            "AlgorithmOverlayDemo", "1", "ManagedFixture", "1", null,
            "SampleDevice", "SampleCamera", "1", "Mono8", "AlreadyNormalized",
            false, false, null, null, milestones);
        return new FrameInput(metadata, provenance, Enumerable.Repeat(marker, Width * Height).ToArray());
    }

    private static AlgorithmPreparationRequest CreatePreparationRequest(AlgorithmDescriptor descriptor,
        AlgorithmConfigurationSnapshot configuration) => new(descriptor.Identity, configuration,
            descriptor.ResultSchema.Id, descriptor.ResultSchema.Version, descriptor.ResultSchema.ContentHash,
            descriptor.ResultSchema.OverlayContract.Id, descriptor.ResultSchema.OverlayContract.Version,
            descriptor.ResultSchema.OverlayContract.ContentHash, TimeSpan.FromSeconds(3));

    private static ProductionStoreOptions CreateOptions(string directory)
    {
        var keyDirectory = Path.Combine(directory, "audit-keys");
        var audit = new AuditIntegrityPolicy(StationId, "development-v1", AuditKeyName)
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = keyDirectory
        };
        var blocklist = PasswordBlocklist.Create("SampleOverlayBlocklist", "1",
            new[] { "overlay-sample-forbidden-value" });
        var identity = new LocalIdentityOptions(StationId,
            new LocalPasswordPolicy { Version = "sample-overlay-password-v1", Blocklist = blocklist },
            new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
            AuthorizationPolicy.Development);
        return new ProductionStoreOptions(Path.Combine(directory, DatabaseName))
        {
            CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(5),
            AuditIntegrityPolicy = audit,
            LocalIdentity = identity,
            AlgorithmResultArchive = new AlgorithmResultArchiveOptions
            {
                MaximumRecordBytes = AlgorithmResultArchiveOptions.DefaultMaximumRecordBytes,
                MaximumTotalBytes = 8L * 1024 * 1024,
                MaximumRecords = 16,
                MaximumPageBytes = AlgorithmResultArchiveOptions.DefaultMaximumPageBytes
            }
        };
    }

    private static void WaitForVerified(IStationRuntime runtime)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            var snapshot = runtime.GetSnapshotAsync().GetAwaiter().GetResult();
            if (snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified) return;
            if (snapshot.AuditIntegrity?.State == AuditIntegrityState.Faulted)
                throw new InvalidOperationException("AlgorithmOverlayAuditVerificationFailed");
            Thread.Sleep(50);
        }
        throw new InvalidOperationException("AlgorithmOverlayAuditVerificationTimedOut");
    }

    private static void DisposeProvider(IServiceProvider provider)
    {
        if (provider is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else if (provider is IDisposable disposable)
            disposable.Dispose();
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private sealed record FrameInput(FrameMetadata Metadata, FrameProvenance Provenance, byte[] Bytes);
    private sealed record ExecutedFrame(AlgorithmExecutionAttempt Attempt, FramePreviewImage? Preview);
    private sealed record OverlayEvidence(int RecordCount, int PrimitiveCount, int RestartRecordCount,
        bool SourceDerivedDistinct, bool PayloadStable, string PayloadHash, string SourcePixelHash,
        string PassRecordId, string ResultSchemaId, string ResultSchemaVersion,
        string ResultSchemaContentHash, string OverlayContractId, string OverlayContractVersion,
        string OverlayContractContentHash, string SourceImage, string DerivedImage, string ViewerImage);

    private sealed class OverlayFactory : IVisionAlgorithmFactory
    {
        public OverlayFactory()
        {
            Descriptor = new AlgorithmDescriptor(new AlgorithmIdentity("Sample.Overlay", "1"),
                new AlgorithmConfigurationSchema("Sample.Overlay.Configuration", "1",
                    Array.Empty<AlgorithmFieldDefinition>()),
                new AlgorithmResultSchema("Sample.Overlay.Result", "1",
                    new[] { new AlgorithmFieldDefinition("Score", AlgorithmScalarType.Float64,
                        "pixel", true, new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 255)) },
                    new[] { "OutOfBoundsOverlay" },
                    new OverlayContract("Sample.Overlay.Geometry", "1", 16, 128, 32, 64)));
        }

        public AlgorithmDescriptor Descriptor { get; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(configuration.Validate(Descriptor.ConfigurationSchema));
        }

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IVisionAlgorithm>(new OverlayAlgorithm(Descriptor));
        }
    }

    private sealed class OverlayAlgorithm : IVisionAlgorithm
    {
        private readonly AlgorithmDescriptor _descriptor;
        private bool _ready;

        public OverlayAlgorithm(AlgorithmDescriptor descriptor) => _descriptor = descriptor;

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ready = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_ready) throw new InvalidOperationException("AlgorithmNotPrepared");
            var marker = context.Frame.GetRowSpan(0)[0];
            var measurement = new AlgorithmMeasurement("Score", "pixel",
                AlgorithmScalarValue.FromFloat64(marker));
            var contract = _descriptor.ResultSchema.OverlayContract;
            return marker switch
            {
                10 => ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Pass, null,
                    new[] { measurement }, new OutputOverlaySet(contract, TenPrimitives()))),
                20 => ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Pass, null,
                    new[] { measurement }, new OutputOverlaySet(contract))),
                30 => ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Pass, null,
                    new[] { measurement }, new OutputOverlaySet(contract,
                        new OverlayPrimitive[] { new OverlayText(new(2, 44), "<script>alert", null,
                            OverlayTextAnchor.BottomLeft) }))),
                _ => ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Unknown,
                    "OutOfBoundsOverlay", new[] { measurement }, new OutputOverlaySet(contract)))
            };
        }

        public ValueTask DisposeAsync()
        {
            _ready = false;
            return ValueTask.CompletedTask;
        }

        private static IReadOnlyList<OverlayPrimitive> TenPrimitives() => new OverlayPrimitive[]
        {
            new OverlayMarker(new(6, 6), OverlayMarkerKind.Cross, 5),
            new OverlayLineSegment(new(-10, 2), new(100, 4)),
            new OverlayArrow(new(4, 8), new(18, 10)),
            new OverlayPolyline(new[] { new OverlayPoint(5, 15), new(10, 12), new(15, 15) }),
            new OverlayPolygon(new[] { new OverlayPoint(20, 4), new(28, 4), new(24, 10) }),
            new OverlayAxisAlignedRectangle(new(30, 4), 10, 8),
            new OverlayRotatedRectangle(new(45, 10), 8, 5, 15),
            new OverlayCircle(new(12, 28), 4),
            new OverlayEllipse(new(28, 28), 6, 3, 10),
            new OverlayText(new(2, 44), "OK", new OverlayStyle(new(220, 40, 40), textSize: 8),
                OverlayTextAnchor.BottomLeft)
        };
    }
}
