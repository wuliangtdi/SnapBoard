using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using SnapBoard.Application.Clipboard;
using SnapBoard.Domain.Clipboard;

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
    private int _thumbnailLoadStarted;
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
        Id = ClipboardItemId.New();
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

    public ClipboardHistoryItemViewModel(ClipboardHistoryItemSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Id = summary.Id;
        Type = summary.DisplayCategory switch
        {
            ClipboardHistoryDisplayCategory.Image => ClipboardItemType.Image,
            ClipboardHistoryDisplayCategory.Code => ClipboardItemType.Code,
            ClipboardHistoryDisplayCategory.Link => ClipboardItemType.Link,
            _ => ClipboardItemType.Text,
        };
        TypeIcon = Type switch
        {
            ClipboardItemType.Image => MaterialIconKind.ImageMultipleOutline,
            ClipboardItemType.Code => MaterialIconKind.CodeTags,
            ClipboardItemType.Link => MaterialIconKind.LinkBoxOutline,
            _ => MaterialIconKind.FormatText,
        };
        SourceIcon = GetSourceIcon(summary.SourceApplication);
        string preview = summary.PreviewText.Trim();
        string[] lines = preview.Split(
            ['\r', '\n'],
            3,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Title = lines.FirstOrDefault() ?? KindLabel;
        Subtitle = lines.Length > 1
            ? lines[1]
            : $"{KindLabel} · {FormatSize(summary.TotalSizeBytes)}";
        SourceApplication = summary.SourceApplication;
        TimestampText = FormatTimestamp(summary.CapturedAt);
        Content = preview;
        LineNumbers = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, Math.Max(1, preview.Split('\n').Length)));
        Language = Type == ClipboardItemType.Code ? "代码" : KindLabel;
        SourceWindow = "—";
        Location = summary.Tags.Count == 0 ? "本机" : string.Join("、", summary.Tags);
        Notes = summary.Tags.Count == 0 ? "—" : string.Join("、", summary.Tags);
        HasThumbnail = summary.HasThumbnail;
        IsPinned = summary.IsPinned;
    }

    public ClipboardItemId Id { get; }

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

    public bool HasThumbnailPlaceholder => HasThumbnail && Thumbnail is null;

    public bool HasColorSwatch { get; }

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    partial void OnThumbnailChanged(Bitmap? value) =>
        OnPropertyChanged(nameof(HasThumbnailPlaceholder));

    internal bool TryBeginThumbnailLoad() =>
        Interlocked.Exchange(ref _thumbnailLoadStarted, 1) == 0;

    internal void ResetThumbnailLoad() =>
        Interlocked.Exchange(ref _thumbnailLoadStarted, 0);

    internal void ReleaseThumbnail()
    {
        Bitmap? thumbnail = Thumbnail;
        Thumbnail = null;
        thumbnail?.Dispose();
    }

    private static MaterialIconKind GetSourceIcon(string sourceApplication)
    {
        if (sourceApplication.Contains("Edge", StringComparison.OrdinalIgnoreCase))
        {
            return MaterialIconKind.MicrosoftEdge;
        }

        if (sourceApplication.Contains("Code", StringComparison.OrdinalIgnoreCase))
        {
            return MaterialIconKind.MicrosoftVisualStudioCode;
        }

        return MaterialIconKind.ApplicationOutline;
    }

    private static string FormatTimestamp(DateTimeOffset capturedAt)
    {
        DateTimeOffset local = capturedAt.ToLocalTime();
        TimeSpan elapsed = DateTimeOffset.Now - local;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
        }

        return local.Date == DateTimeOffset.Now.Date
            ? $"今天 {local:HH:mm}"
            : local.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:F1} KB",
        _ => $"{bytes / (1024d * 1024d):F1} MB",
    };
}
