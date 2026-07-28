namespace SnapBoard.Sync.Contracts;

/// <summary>空间内同步的非敏感应用设置。正文仍随事件信封加密后上传。</summary>
public sealed record SyncSettingPayload(
    string Key,
    string Value);
