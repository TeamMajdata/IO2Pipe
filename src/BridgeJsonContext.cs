using System.Text.Json;
using System.Text.Json.Serialization;

namespace IO2Pipe;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BridgeOptions))]
internal partial class BridgeJsonContext : JsonSerializerContext
{
}
