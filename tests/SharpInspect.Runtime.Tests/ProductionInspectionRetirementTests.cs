using System.Diagnostics;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V142_R12_LateAlgorithmRetirementKeepsCorePendingAndPreventsAnyPublication()
    {
        const string childFlag = "SHARPINSPECT_V142_RETIREMENT_CHILD";
        if (Environment.GetEnvironmentVariable(childFlag) != "1")
        {
            // AlgorithmHung is intentionally process-wide and permanent. Run the
            // real hung invocation in its own host, as required for controlled recovery.
            var results = Path.Combine(Path.GetTempPath(), "v142-retirement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(results);
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment[childFlag] = "1";
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(ManualInspectionRuntimeTests).Assembly.Location);
            start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=SharpInspect.Runtime.Tests.ManualInspectionRuntimeTests." +
                nameof(V142_R12_LateAlgorithmRetirementKeepsCorePendingAndPreventsAnyPublication));
            start.ArgumentList.Add("/Logger:trx;LogFileName=retirement.trx");
            start.ArgumentList.Add("/ResultsDirectory:" + results);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            var transcript = await output + await error;
            await File.WriteAllTextAsync(Path.Combine(results, "process.log"), transcript);
            Assert.True(process.ExitCode == 0, transcript);
            var trx = System.Xml.Linq.XDocument.Load(Path.Combine(results, "retirement.trx"));
            var cases = trx.Descendants().Where(value => value.Name.LocalName == "UnitTestResult").ToArray();
            Assert.Equal("Passed", Assert.Single(cases).Attribute("outcome")?.Value);
            Console.WriteLine("V142_R12 isolated evidence: " + results + "; trx SHA256=" +
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(results, "retirement.trx")))));
            return;
        }
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Ready before late algorithm");
        harness.Factory.HoldExecution();
        try
        {
            peer.RaiseTrigger(61, 1);
            await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
            var corePage = await WaitForProductionHistoryAsync(harness,
                page => page.Events.Any(value => value.Kind == ProductionInspectionEventKind.CoreCommitted),
                "Logical timeout must commit Core while physical algorithm remains owned");
            var coreEvent = Assert.Single(corePage.Events, value => value.Kind == ProductionInspectionEventKind.CoreCommitted);
            Assert.Equal(ExecutionStatus.Timeout, coreEvent.Core!.ExecutionStatus);
            var faulted = await WaitForProductionHistoryAsync(harness,
                page => page.Events.Any(value => value.Kind == ProductionInspectionEventKind.FaultTerminated),
                "Physical retirement timeout must terminate the pending Core");
            Assert.DoesNotContain(faulted.Events, value => value.Kind is
                ProductionInspectionEventKind.PublicationPrepared or ProductionInspectionEventKind.ResultValidRaised or
                ProductionInspectionEventKind.ResultAcknowledged or ProductionInspectionEventKind.AcknowledgementReset);
            Assert.Equal(0, peer.ResultValidHighCount);
            Assert.Equal(0, CountProductionPayloadWrites(peer));
            Assert.Equal(0, peer.AckLowCount);
            Assert.False(peer.RuntimeResultValid);
            var cold = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(cold.Available, cold.ReasonCode);
            Assert.True(cold.RecoveryRequired);
            Assert.Equal(coreEvent.Core.ContentHash, cold.Latest!.Core!.ContentHash);
        }
        finally { harness.Factory.ReleaseExecution(); }
    }
}
