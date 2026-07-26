using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace SnapBoard.Desktop.ViewModels;

public enum ClipboardItemType
{
    Text,
    Image,
    Code,
    Link,
}

public sealed partial class ClipboardHistoryItemViewModel : ObservableObject
{
    public ClipboardHistoryItemViewModel(
        ClipboardItemType type,
        MaterialIconKind typeIcon,
        MaterialIconKind sourceIcon,
        string title,
        string subtitle,
        string sourceApplication,
        string timestampText,
        string content,
        string language,
        string sourceWindow,
        string location,
        string notes = "—",
        bool hasThumbnail = false,
        bool hasColorSwatch = false)
    {
        Type = type;
        TypeIcon = typeIcon;
        SourceIcon = sourceIcon;
        Title = title;
        Subtitle = subtitle;
        SourceApplication = sourceApplication;
        TimestampText = timestampText;
        Content = content;
        Language = language;
        SourceWindow = sourceWindow;
        Location = location;
        Notes = notes;
        HasThumbnail = hasThumbnail;
        HasColorSwatch = hasColorSwatch;
        LineNumbers = string.Join(Environment.NewLine, Enumerable.Range(1, Math.Max(1, content.Split('\n').Length)));
    }

    public ClipboardItemType Type { get; }

    public string KindLabel => Type switch
    {
        ClipboardItemType.Text => "文本",
        ClipboardItemType.Image => "图片",
        ClipboardItemType.Code => "代码",
        ClipboardItemType.Link => "链接",
        _ => "未知",
    };

    public MaterialIconKind TypeIcon { get; }

    public MaterialIconKind SourceIcon { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string SourceApplication { get; }

    public string TimestampText { get; }

    public string Content { get; }

    public string LineNumbers { get; }

    public string Language { get; }

    public string CharacterCountText => $"{Content.Length} 字符";

    public string SourceWindow { get; }

    public string Location { get; }

    public string Notes { get; }

    public bool HasThumbnail { get; }

    public bool HasColorSwatch { get; }

    [ObservableProperty]
    public partial bool IsPinned { get; set; }
}
