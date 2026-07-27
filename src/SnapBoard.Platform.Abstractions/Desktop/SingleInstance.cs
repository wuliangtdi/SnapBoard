namespace SnapBoard.Platform.Abstractions.Desktop;

public enum SingleInstanceCommand : byte
{
    ActivateMainWindow = 1,
    ShowQuickWindow = 2,
    ShowSettingsWindow = 3,
    Exit = 4,
    RemainInBackground = 5,
    CloseWindows = 6,
}
