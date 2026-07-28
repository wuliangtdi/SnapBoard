using System.Text.Json.Serialization;

namespace SnapBoard.Infrastructure.Storage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(StorageLocationDocument))]
[JsonSerializable(typeof(StorageInstanceDocument))]
[JsonSerializable(typeof(StorageMigrationManifest))]
[JsonSerializable(typeof(StorageMigrationStateDocument))]
[JsonSerializable(typeof(StorageStartupAcknowledgementDocument))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
