using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Hikrobot;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Hikrobot.DeviceProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() } };

    private static async Task<int> Main(string[] args)
    {
        var nativeSdkLoadAttempted = false;
        try
        {
            using var clock = new SystemFrameAcquisitionClock();
            var poolOptions = new FrameBufferPoolOptions(2, 16 * 1024 * 1024, TimeSpan.FromMilliseconds(100));
            if (args.Length == 0 || args.SequenceEqual(new[] { "--diagnose" }))
            {
                await using var provider = new HikrobotCameraProvider(clock, poolOptions);
                var open = await provider.OpenAsync("diagnostic-no-device-access");
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    verificationId = "V121-N01", result = "Pass", nativeSdkLoaded = false,
                    deviceAccess = "NotRun", hardwareQualification = "NotRun", productionReady = false,
                    productionOpenSucceeded = open.Succeeded, productionOpenReason = open.ReasonCode,
                    dependency = provider.DependencyReport
                }, Json));
                return open.Succeeded || provider.DependencyReport.ProductionCompatible ? 1 : 0;
            }
            if (args.Length != 13 || args[0] != "--allow-device-access")
                throw new ArgumentException("HikrobotProbeExplicitDeviceAccessAndAllBindingsRequired");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index += 2)
                if (!values.TryAdd(args[index], args[index + 1])) throw new ArgumentException("HikrobotProbeDuplicateOption");
            var keys = new[] { "--runtime", "--runtime-sha256", "--device", "--model", "--configuration", "--output" };
            if (values.Count != keys.Length || keys.Any(key => !values.ContainsKey(key)))
                throw new ArgumentException("HikrobotProbeBindingsMissing");
            var runtimePath = RequireFile(values["--runtime"]);
            var configurationPath = RequireFile(values["--configuration"]);
            var outputPath = values["--output"];
            if (!Path.IsPathFullyQualified(outputPath)) throw new ArgumentException("HikrobotProbeAbsoluteOutputRequired");
            if (Directory.Exists(outputPath) || File.Exists(outputPath))
                throw new ArgumentException("HikrobotProbeNewOutputDirectoryRequired");
            // Keep the bound file open without write/delete sharing through SDK retirement.
            // Hash incrementally so a mistaken path cannot allocate an arbitrary-sized array.
            using var runtimeFile = new FileStream(runtimePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var runtimeLength = runtimeFile.Length;
            if (runtimeLength is < 1 or > 64 * 1024 * 1024)
                throw new ArgumentException("HikrobotProbeRuntimeSizeInvalid");
            using var runtimeHash = SHA256.Create();
            if (values["--runtime-sha256"].Length != 64 ||
                !Convert.ToHexString(runtimeHash.ComputeHash(runtimeFile))
                    .Equals(values["--runtime-sha256"], StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("HikrobotProbeRuntimeHashMismatch");
            if (runtimeFile.Length != runtimeLength)
                throw new ArgumentException("HikrobotProbeRuntimeSizeChanged");
            using var configurationFile = new FileStream(configurationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var configurationLength = configurationFile.Length;
            if (configurationLength is < 1 or > 8192)
                throw new ArgumentException("HikrobotProbeConfigurationSizeInvalid");
            using var configurationReader = new BinaryReader(configurationFile, System.Text.Encoding.UTF8, leaveOpen: true);
            var configurationBytes = configurationReader.ReadBytes((int)configurationLength);
            if (configurationBytes.Length != configurationLength || configurationFile.Length != configurationLength)
                throw new ArgumentException("HikrobotProbeConfigurationSizeChanged");
            using (var document = JsonDocument.Parse(configurationBytes))
            {
                var configurationKeys = new[] { "productionAcquisitionMode", "exposureTimeUs", "gainDb", "regionOfInterest",
                    "pixelFormat", "validBits", "acquisitionTimeoutMs", "triggerDelayUs", "whiteBalanceRgb" };
                var actual = document.RootElement.EnumerateObject().Select(value => value.Name).ToArray();
                if (actual.Length != configurationKeys.Length || actual.Distinct().Count() != actual.Length ||
                    actual.Except(configurationKeys).Any()) throw new ArgumentException("HikrobotProbeConfigurationFieldsInvalid");
            }
            var requested = JsonSerializer.Deserialize<RequestedCameraConfiguration>(configurationBytes, Json)
                ?? throw new ArgumentException("HikrobotProbeConfigurationMissing");
            if (requested.ProductionAcquisitionMode == ProductionAcquisitionMode.HardwareTrigger)
                throw new ArgumentException("HikrobotHardwareTriggerNotQualified");
            if (requested.AcquisitionTimeoutMs > 10_000)
                throw new ArgumentException("HikrobotProbeFrameDeadlineTooLong");
            Directory.CreateDirectory(outputPath);
            // This is the only executable entry into native SDK/device operations. The
            // launcher runs it in a separate bounded process after explicit authorization.
            nativeSdkLoadAttempted = true;
            using var sdk = new HikrobotNativeRuntime(runtimePath);
            await using var qualification = new HikrobotCameraProvider(sdk, clock, poolOptions);
            var discovery = await qualification.DiscoverAsync();
            var expected = discovery.Devices.SingleOrDefault(value => value.StableDeviceIdentity == values["--device"]);
            if (!discovery.Succeeded || expected is null || (expected.ReportedModel ?? "Unavailable") != values["--model"])
                throw new InvalidOperationException("HikrobotProbeDiscoveryBindingMismatch");
            var opened = await qualification.OpenAsync(expected.StableDeviceIdentity);
            if (!opened.Succeeded || opened.Device is not IControlledCameraDevice device)
                throw new InvalidOperationException(opened.ReasonCode);
            var applied = await device.ApplyConfigurationAsync(requested);
            if (!applied.Succeeded) throw new InvalidOperationException(applied.ReasonCode);
            var started = await device.StartAsync();
            if (!started.Succeeded) throw new InvalidOperationException(started.ReasonCode);
            await using var acquisition = new CameraAcquisitionService(device, applied.Effective!, clock,
                new CameraAcquisitionOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
            var attempt = await acquisition.AcquireAsync(ExecutionKind.Qualification, "probe-camera");
            using var outcome = attempt.Outcome;
            if (!attempt.Accepted || outcome?.Succeeded != true || outcome.Lease is null)
                throw new InvalidOperationException(outcome?.ReasonCode ?? attempt.ReasonCode);
            var lease = outcome.Lease;
            string pixelsHash;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                for (var row = 0; row < lease.Frame.Height; row++) hash.AppendData(lease.Frame.GetRowSpan(row));
                pixelsHash = Convert.ToHexString(hash.GetHashAndReset());
            }
            var evidence = new
            {
                verificationId = "V121-Q02", result = "Pass", developmentDeviceObservation = true,
                hardwareQualification = "NotRun", productionReady = acquisition.Ready,
                candidateRuntime = sdk.RuntimeVersion, runtimeSha256 = values["--runtime-sha256"],
                configurationSha256 = Convert.ToHexString(SHA256.HashData(configurationBytes)),
                device = expected, frame = lease.Frame.Metadata, provenance = lease.Provenance, pixelsSha256 = pixelsHash
            };
            File.WriteAllText(Path.Combine(outputPath, "device-observation.json"), JsonSerializer.Serialize(evidence, Json));
            Console.WriteLine(JsonSerializer.Serialize(evidence, Json));
            return acquisition.Ready ? 1 : 0;
        }
        catch (Exception exception)
        {
            var reason = exception is HikrobotSdkException sdk ? sdk.ReasonCode :
                exception.Message.StartsWith("Hikrobot", StringComparison.Ordinal) ? exception.Message : "HikrobotProbeFailed";
            Console.Error.WriteLine(JsonSerializer.Serialize(new { result = "Fail", reasonCode = reason,
                nativeSdkLoadAttempted, hardwareQualification = "NotRun", productionReady = false }, Json));
            return 1;
        }
    }

    private static string RequireFile(string path) => Path.IsPathFullyQualified(path) && File.Exists(path)
        ? Path.GetFullPath(path) : throw new ArgumentException("HikrobotProbeAbsoluteExistingFileRequired");
}
