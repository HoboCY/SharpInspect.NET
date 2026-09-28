param([string]$ArtifactPath = 'E:\temp\SharpInspect.NET-validation-artifacts\ticket24\20260909-023020-128\calibration-consumer\bin\Release\net6.0-windows')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
public static class PublishedT24Capture
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object Make(Type type, params object[] values) =>
        type.GetConstructors(Flags).Single(c => c.GetParameters().Length == values.Length).Invoke(values);
    public static string Capture(string directory)
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "SharpInspect.Abstractions" || a.GetName().Name == "SharpInspect.Runtime"))
            throw new InvalidOperationException("Fresh process required");
        var abstractionsPath = Path.Combine(directory, "SharpInspect.Abstractions.dll");
        var runtimePath = Path.Combine(directory, "SharpInspect.Runtime.dll");
        var a = Assembly.LoadFrom(abstractionsPath);
        var r = Assembly.LoadFrom(runtimePath);
        Func<string, Type> at = name => a.GetType("SharpInspect.Abstractions." + name, true);
        Func<string, Type> rt = name => r.GetType("SharpInspect.Runtime.Calibration." + name, true);
        var contract = Make(at("RecipeContractReference"), "coeff-format", "1", new string('A', 64));
        var coefficient = Make(at("CalibrationCoefficientPayload"), contract, new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }));
        var result = Make(at("CalibrationProcedureComputationResult"), coefficient, null, null);
        var session = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var occurred = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var candidate = Make(at("CalibrationCandidateEvidence"), Guid.Parse("11111111-1111-1111-1111-111111111111"),
            session, new string('3',64), new string('4',64), result, occurred);
        var ev = Make(rt("CalibrationSessionEvent"), Guid.Parse("33333333-3333-3333-3333-333333333333"),
            session, Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Enum.Parse(at("CalibrationSessionPhase"), "CandidateRetained"), Enum.Parse(at("CalibrationSessionOutcome"), "Pending"),
            "CandidateRetained", occurred, null, null, null, candidate, null, null);
        var bytes = (byte[])rt("CalibrationSessionStorageCodec").GetMethod("EncodeEvent", Flags).Invoke(null, new[] { ev });
        return "HOST_FRAMEWORK=" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + "\n" +
            "ABSTRACTIONS_PATH=" + abstractionsPath + "\nABSTRACTIONS_SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(abstractionsPath))) + "\n" +
            "RUNTIME_PATH=" + runtimePath + "\nRUNTIME_SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtimePath))) + "\n" +
            "CANDIDATE_HASH=" + at("CalibrationCandidateEvidence").GetProperty("ContentHash").GetValue(candidate) + "\n" +
            "PAYLOAD_LENGTH=" + bytes.Length + "\nPAYLOAD_SHA256=" + Convert.ToHexString(SHA256.HashData(bytes)) + "\n" +
            "PAYLOAD_BASE64=" + Convert.ToBase64String(bytes) + "\nPAYLOAD_UTF8=" + Encoding.UTF8.GetString(bytes);
    }
}
"@
[PublishedT24Capture]::Capture([IO.Path]::GetFullPath($ArtifactPath))
