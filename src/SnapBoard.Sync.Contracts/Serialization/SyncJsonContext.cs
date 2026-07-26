using System.Text.Json.Serialization;

namespace SnapBoard.Sync.Contracts.Serialization;

/// <summary>
/// 同步协议唯一允许使用的 JSON 元数据入口。源生成避免运行时反射，
/// 可在 Native AOT 和裁剪发布中保持字段契约稳定。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SyncEventEnvelope))]
public partial class SyncJsonContext : JsonSerializerContext;
