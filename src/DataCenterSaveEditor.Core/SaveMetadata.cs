using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataCenterSaveEditor.Core;

public sealed record SaveMetadata(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("nameOfSave")] string NameOfSave);

internal static class SaveMetadataSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static SaveMetadata Parse(ReadOnlySpan<byte> bytes)
    {
        SaveMetadata? metadata = JsonSerializer.Deserialize<SaveMetadata>(bytes, Options);
        return metadata ?? throw new InvalidDataException("The metadata file is empty or invalid.");
    }

    public static byte[] Serialize(SaveMetadata metadata) => JsonSerializer.SerializeToUtf8Bytes(metadata, Options);
}
