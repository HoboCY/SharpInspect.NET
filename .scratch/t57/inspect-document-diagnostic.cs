using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Performance;
class Probe
{
    static void Main(string[] args)
    {
        var bytes = File.ReadAllBytes(args[0]);
        using var json = JsonDocument.Parse(bytes);
        var method = typeof(PerformanceEvidenceDocuments).GetMethod("ReadClosed", BindingFlags.NonPublic | BindingFlags.Static);
        var raw = (PerformanceRawCapture)method.Invoke(null, new object[] { typeof(PerformanceRawCapture), json.RootElement.GetProperty("Raw") });
        var encoded = PerformanceEvidenceDocuments.Encode(raw);
        File.WriteAllBytes(args[1], encoded);
        Console.WriteLine("CLR=" + Environment.Version + " Culture=" + System.Globalization.CultureInfo.CurrentCulture.Name);
        Console.WriteLine("Equal=" + bytes.AsSpan().SequenceEqual(encoded));
        try { PerformanceEvidenceDocuments.Decode(bytes); Console.WriteLine("Decode=Pass"); } catch (Exception ex) { Console.WriteLine(ex); }
    }
}
