using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

internal static partial class RecipeDraftStorageCodec
{
    /// <summary>Locally registered schema only; this metadata is never copied into the exchange package.</summary>
    internal static string EncodeSchemaForTransfer(AlgorithmConfigurationSchema schema)
    {
        using var buffer = new BoundedBufferWriter(MaximumPayloadBytes);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteSchema(writer, schema);
            writer.Flush();
        }
        return StrictUtf8String(buffer.ToArray());
    }
}
