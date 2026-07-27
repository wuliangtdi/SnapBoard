namespace SnapBoard.Platform.Abstractions.Desktop;

/// <summary>
/// 平台启动上下文边界。原生 Apple Event 或会话参数不得进入 Desktop 生命周期逻辑。
/// </summary>
public interface ILaunchContextService
{
    bool WasLaunchedAsLoginItem();
}
