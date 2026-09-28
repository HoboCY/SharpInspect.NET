global using System;
global using System.IO;
global using System.Linq;
global using System.Collections.Generic;
global using System.Threading;
global using System.Threading.Tasks;
using System.Reflection;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Performance;
class CanonicalProbe
{
    static int Main(string[] args)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(args[0]));
        var reader = typeof(PerformanceEvidenceDocuments).GetMethod("ReadClosed", BindingFlags.NonPublic | BindingFlags.Static);
        var raw = (PerformanceRawCapture)reader.Invoke(null, new object[] { typeof(PerformanceRawCapture), json.RootElement.GetProperty("Raw") });
        var encoded = PerformanceEvidenceDocuments.Encode(raw);
        File.WriteAllBytes(args[1], encoded);
        var decoded = PerformanceEvidenceDocuments.Decode(encoded);
        Console.WriteLine($"CLR={Environment.Version}; Passed={decoded.Report.Passed}; RawHash={decoded.RawHash}; ReportHash={decoded.ReportHash}");
        return decoded.Report.Passed ? 0 : 3;
    }
}
