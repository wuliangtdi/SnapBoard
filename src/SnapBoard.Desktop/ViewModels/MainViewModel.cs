using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace SnapBoard.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly List<ClipboardHistoryItemViewModel> _allItems;

    public MainViewModel()
    {
        // Phase 1.2 尚未接入真实剪贴板用例，这组脱敏数据用于先验证列表密度、
        // 搜索、筛选、选择和预览等核心 UI 状态，后续由 Application 层查询结果替换。
        _allItems = CreateSampleItems();
        RefreshVisibleItems();
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ClipboardItemType? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial ClipboardHistoryItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool IsNewestFirst { get; set; } = true;

    [ObservableProperty]
    public partial string LastSyncText { get; set; } = "已同步";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "就绪";

    [ObservableProperty]
    public partial bool IsCompactMode { get; set; }

    [ObservableProperty]
    public partial GridLength HistoryColumnWidth { get; set; } = new(49, GridUnitType.Star);

    [ObservableProperty]
    public partial GridLength PreviewColumnWidth { get; set; } = new(51, GridUnitType.Star);

    public ObservableCollection<ClipboardHistoryItemViewModel> VisibleItems { get; } = [];

    public string ProductName { get; } = "SnapBoard";

    public string ProductNameChinese { get; } = "闪剪";

    public string SearchWatermark { get; } = "搜索剪贴板记录";

    public string RecordCountText { get; } = "共 128 条记录";

    public string DeviceName { get; } = "本机";

    public string SortLabel => IsNewestFirst ? "排序：最新优先" : "排序：最早优先";

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool HasVisibleItems => VisibleItems.Count > 0;

    public bool HasNoVisibleItems => VisibleItems.Count == 0;

    public bool IsAllFilterSelected => SelectedFilter is null;

    public bool IsTextFilterSelected => SelectedFilter == ClipboardItemType.Text;

    public bool IsImageFilterSelected => SelectedFilter == ClipboardItemType.Image;

    public bool IsCodeFilterSelected => SelectedFilter == ClipboardItemType.Code;

    public bool IsLinkFilterSelected => SelectedFilter == ClipboardItemType.Link;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        RefreshVisibleItems();
    }

    partial void OnSelectedFilterChanged(ClipboardItemType? value)
    {
        OnPropertyChanged(nameof(IsAllFilterSelected));
        OnPropertyChanged(nameof(IsTextFilterSelected));
        OnPropertyChanged(nameof(IsImageFilterSelected));
        OnPropertyChanged(nameof(IsCodeFilterSelected));
        OnPropertyChanged(nameof(IsLinkFilterSelected));
        RefreshVisibleItems();
    }

    partial void OnSelectedItemChanged(ClipboardHistoryItemViewModel? value)
    {
        if (value is not null)
        {
            StatusMessage = $"已选择{value.KindLabel}记录";
        }
    }

    partial void OnIsNewestFirstChanged(bool value)
    {
        OnPropertyChanged(nameof(SortLabel));
    }

    [RelayCommand]
    private void SelectFilter(string? filterName)
    {
        SelectedFilter = filterName switch
        {
            "Text" => ClipboardItemType.Text,
            "Image" => ClipboardItemType.Image,
            "Code" => ClipboardItemType.Code,
            "Link" => ClipboardItemType.Link,
            _ => null,
        };
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ToggleSort()
    {
        IsNewestFirst = !IsNewestFirst;
        RefreshVisibleItems();
    }

    [RelayCommand]
    private void Sync()
    {
        LastSyncText = "刚刚同步";
        StatusMessage = "同步完成";
    }

    [RelayCommand]
    private void ToggleCompactMode()
    {
        IsCompactMode = !IsCompactMode;
        HistoryColumnWidth = IsCompactMode
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(49, GridUnitType.Star);
        PreviewColumnWidth = IsCompactMode
            ? new GridLength(0)
            : new GridLength(51, GridUnitType.Star);
        StatusMessage = IsCompactMode ? "已切换到紧凑模式" : "已展开内容预览";
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (SelectedItem is null)
        {
            return;
        }

        SelectedItem.IsPinned = !SelectedItem.IsPinned;
        StatusMessage = SelectedItem.IsPinned ? "已置顶" : "已取消置顶";
    }

    [RelayCommand]
    private void Copy()
    {
        if (SelectedItem is not null)
        {
            StatusMessage = "已复制到剪贴板";
        }
    }

    [RelayCommand]
    private void Paste()
    {
        if (SelectedItem is not null)
        {
            StatusMessage = "已准备粘贴";
        }
    }

    [RelayCommand]
    private void OpenSelectedItem()
    {
        if (SelectedItem is not null)
        {
            StatusMessage = "已打开所选记录";
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedItem is null)
        {
            return;
        }

        int selectedIndex = VisibleItems.IndexOf(SelectedItem);
        _allItems.Remove(SelectedItem);
        RefreshVisibleItems();

        if (VisibleItems.Count > 0)
        {
            SelectedItem = VisibleItems[Math.Clamp(selectedIndex, 0, VisibleItems.Count - 1)];
        }

        StatusMessage = "记录已移除";
    }

    [RelayCommand]
    private void SetStatus(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message;
        }
    }

    private void RefreshVisibleItems()
    {
        // 搜索和类型过滤在内存中合并执行，保持 View 只消费一个稳定集合。
        // 接入真实仓储后，这个入口仍保留，但查询、分页和取消令牌下沉到 Application 层。
        IEnumerable<ClipboardHistoryItemViewModel> query = _allItems;

        if (SelectedFilter is { } selectedFilter)
        {
            query = query.Where(item => item.Type == selectedFilter);
        }

        string search = SearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(item =>
                item.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Content.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.SourceApplication.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (!IsNewestFirst)
        {
            query = query.Reverse();
        }

        ClipboardHistoryItemViewModel? previousSelection = SelectedItem;
        VisibleItems.Clear();
        foreach (ClipboardHistoryItemViewModel item in query)
        {
            VisibleItems.Add(item);
        }

        SelectedItem = previousSelection is not null && VisibleItems.Contains(previousSelection)
            ? previousSelection
            : VisibleItems.FirstOrDefault();

        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(HasNoVisibleItems));
    }

    private static List<ClipboardHistoryItemViewModel> CreateSampleItems()
    {
        const string mainViewModelCode = """
            using CommunityToolkit.Mvvm.ComponentModel;
            using CommunityToolkit.Mvvm.Input;

            public partial class MainViewModel : ObservableObject
            {
                [ObservableProperty] private string? _input;
                [RelayCommand] private void Clear() => Input = string.Empty;
            }
            """;

        return
        [
            new(
                ClipboardItemType.Code,
                MaterialIconKind.CodeTags,
                MaterialIconKind.MicrosoftVisualStudioCode,
                "public partial class MainViewModel : ObservableObject",
                "使用 CommunityToolkit.Mvvm 实现属性与命令。",
                "VS Code",
                "刚刚",
                mainViewModelCode,
                "C#",
                "Program.cs",
                "剪贴板历史"),
            new(
                ClipboardItemType.Text,
                MaterialIconKind.FormatText,
                MaterialIconKind.MicrosoftOffice,
                "Avalonia 是一个跨平台 UI 框架，用于构建现代化的桌面、移动和浏览器应用程序。",
                "使用 C# 和 XAML 构建高性能应用。",
                "WPS Office",
                "2 分钟前",
                "Avalonia 是一个跨平台 UI 框架，用于构建现代化的桌面、移动和浏览器应用程序，使用 C# 和 XAML。",
                "纯文本",
                "需求说明.docx",
                "剪贴板历史"),
            new(
                ClipboardItemType.Link,
                MaterialIconKind.LinkBoxOutline,
                MaterialIconKind.MicrosoftEdge,
                "Avalonia Documentation",
                "https://docs.avaloniaui.net/",
                "Microsoft Edge",
                "5 分钟前",
                "https://docs.avaloniaui.net/",
                "URL",
                "Avalonia Documentation",
                "剪贴板历史"),
            new(
                ClipboardItemType.Image,
                MaterialIconKind.ImageMultipleOutline,
                MaterialIconKind.ImageMultipleOutline,
                "截图 2026-07-26 18.42.31.png",
                "PNG · 1920 × 1080",
                "截图工具",
                "8 分钟前",
                "截图 2026-07-26 18.42.31.png",
                "PNG",
                "截图工具",
                "剪贴板历史",
                hasThumbnail: true),
            new(
                ClipboardItemType.Text,
                MaterialIconKind.PaletteOutline,
                MaterialIconKind.FolderOpenOutline,
                "#0078D4",
                "颜色值",
                "Windows 资源管理器",
                "12 分钟前",
                "#0078D4",
                "颜色",
                "颜色选择器",
                "剪贴板历史",
                hasColorSwatch: true),
            new(
                ClipboardItemType.Text,
                MaterialIconKind.Console,
                MaterialIconKind.Console,
                "git commit -m \"feat: add clipboard sync\"",
                "git push origin main",
                "Windows Terminal",
                "20 分钟前",
                "git commit -m \"feat: add clipboard sync\"\ngit push origin main",
                "Shell",
                "Windows Terminal",
                "剪贴板历史"),
            new(
                ClipboardItemType.Code,
                MaterialIconKind.CodeTags,
                MaterialIconKind.MicrosoftVisualStudioCode,
                "Console.WriteLine(\"Hello, SnapBoard!\");",
                "C# · 31 字符",
                "VS Code",
                "今天 17:58",
                "Console.WriteLine(\"Hello, SnapBoard!\");",
                "C#",
                "Program.cs",
                "剪贴板历史"),
            new(
                ClipboardItemType.Text,
                MaterialIconKind.FormatText,
                MaterialIconKind.MicrosoftOffice,
                "需求评审会议纪要：",
                "1. 支持搜索历史记录  2. 快速复制与粘贴  3. 跨设备同步",
                "WPS Office",
                "今天 17:16",
                "需求评审会议纪要：\n1. 支持搜索历史记录\n2. 快速复制与粘贴\n3. 跨设备同步",
                "纯文本",
                "会议纪要.docx",
                "剪贴板历史"),
            new(
                ClipboardItemType.Link,
                MaterialIconKind.LinkBoxOutline,
                MaterialIconKind.MicrosoftEdge,
                ".NET 10 发布说明（预览）",
                "https://learn.microsoft.com/zh-cn/dotnet/core/whats-new/dotnet-10/overview",
                "Microsoft Edge",
                "今天 16:42",
                "https://learn.microsoft.com/zh-cn/dotnet/core/whats-new/dotnet-10/overview",
                "URL",
                ".NET 10 发布说明",
                "剪贴板历史"),
            new(
                ClipboardItemType.Image,
                MaterialIconKind.ImageMultipleOutline,
                MaterialIconKind.ImageMultipleOutline,
                "产品路线图 Q3",
                "剪贴板云同步、快捷键配置、更多格式支持",
                "截图工具",
                "今天 15:34",
                "产品路线图 Q3",
                "PNG",
                "路线图.png",
                "剪贴板历史",
                hasThumbnail: true),
        ];
    }
}
