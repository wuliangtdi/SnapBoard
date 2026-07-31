using System.Text.Json.Serialization;

namespace SnapBoard.Sync.Contracts.Serialization;

/// <summary>
/// 同步协议唯一允许使用的 JSON 元数据入口。源生成避免运行时反射，
/// 可在 Native AOT 和裁剪发布中保持字段契约稳定。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(SyncEventEnvelope))]
[JsonSerializable(typeof(SyncClipboardItemPayload))]
[JsonSerializable(typeof(SyncRepresentationPayload))]
[JsonSerializable(typeof(SyncRepresentationPayload[]))]
[JsonSerializable(typeof(SyncBlobReferencePayload))]
[JsonSerializable(typeof(SyncSourceApplicationIconPayload))]
[JsonSerializable(typeof(SyncSpaceMetadata))]
[JsonSerializable(typeof(SyncDeviceCheckpoint))]
[JsonSerializable(typeof(SyncProviderMigrationIntent))]
[JsonSerializable(typeof(SyncProviderMigrationDeviceMarker))]
[JsonSerializable(typeof(SyncProviderMigrationDecision))]
[JsonSerializable(typeof(SyncProviderMigrationCheckpoint))]
[JsonSerializable(typeof(SyncProviderMigrationCheckpoint[]))]
[JsonSerializable(typeof(SyncEncryptedObjectEnvelope))]
[JsonSerializable(typeof(SyncRecoveryEnvelope))]
[JsonSerializable(typeof(SyncSettingPayload))]
[JsonSerializable(typeof(string[]))]
public partial class SyncJsonContext : JsonSerializerContext;
