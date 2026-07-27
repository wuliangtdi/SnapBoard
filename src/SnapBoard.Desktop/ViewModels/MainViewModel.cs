using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using SnapBoard.Application.Clipboard;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Desktop.ViewModels;

public sealed record ClipboardSelectedWriteRequest(
    ClipboardItemId ItemId,
    ClipboardWriteRequest Request);

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 50;
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(150);
    private readonly List<ClipboardHistoryItemViewModel> _designItems;
    private readonly IClipboardHistoryService? _historyService;
    private readonly IClipboardSourceApplicationMetadataResolver? _sourceMetadataResolver;
    private readonly object _historyReloadGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SynchronizationContext? _uiContext;
    private ClipboardHistoryCursor? _nextCursor;
    private CancellationTokenSource? _queryCancellation;
    private Timer? _historyReloadTimer;
    private long _historyReloadDueAtMilliseconds;
    private Task _pendingOperation = Task.CompletedTask;
    private long _historyChangeVersion;
    private long _queryGeneration;
    private long _totalCount;
    private int _disposed;
    private int _started;

    public MainViewModel()
    {
        // 参数less 构造仅供 XAML Design.DataContext 与无数据库视觉测试使用；
        // 正式组合根始终选择下面的 Application 用例构造函数。
        _uiContext = SynchronizationContext.Current;
        _designItems = CreateSampleItems();
        RefreshDesignItems();
    }

    public MainViewModel(IClipboardHistoryService historyService)
    {
        ArgumentNullException.ThrowIfNull(historyService);
        _uiContext = SynchronizationContext.Current;
        _historyService = historyService;
        _designItems = [];
        _historyService.HistoryChanged += OnHistoryChanged;
    }

    public MainViewModel(
        IClipboardHistoryService historyService,
        IClipboardSourceApplicationMetadataResolver sourceMetadataResolver)
    {
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(sourceMetadataResolver);
        _uiContext = SynchronizationContext.Current;
        _historyService = historyService;
        _sourceMetadataResolver = sourceMetadataResolver;
        _designItems = [];
        _historyService.HistoryChanged += OnHistoryChanged;
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
    public partial bool IsRecordingPaused { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool CanLoadMore { get; set; }

    [ObservableProperty]
    public partial GridLength HistoryColumnWidth { get; set; } = new(49, GridUnitType.Star);

    [ObservableProperty]
    public partial GridLength PreviewColumnWidth { get; set; } = new(51, GridUnitType.Star);

    public ObservableCollection<ClipboardHistoryItemViewModel> VisibleItems { get; } = [];

    public string ProductName { get; } = "SnapBoard";

    public string ProductNameChinese { get; } = "闪剪";

    public string SearchWatermark { get; } = "搜索剪贴板记录";

    public string RecordCountText => _totalCount >= 0
        ? $"共 {_totalCount:N0} 条记录"
        : $"已加载 {VisibleItems.Count:N0} 条记录";

    public string DeviceName { get; } = "本机";

    public string SortLabel => IsNewestFirst ? "排序：最新优先" : "排序：最早优先";

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool HasVisibleItems => VisibleItems.Count > 0;

    public bool HasNoVisibleItems => !IsLoading && VisibleItems.Count == 0;

    public bool IsAllFilterSelected => SelectedFilter is null;

    public bool IsTextFilterSelected => SelectedFilter == ClipboardItemType.Text;

    public bool IsImageFilterSelected => SelectedFilter == ClipboardItemType.Image;

    public bool IsCodeFilterSelected => SelectedFilter == ClipboardItemType.Code;

    public bool IsLinkFilterSelected => SelectedFilter == ClipboardItemType.Link;

    public string RecordingStateText => IsRecordingPaused ? "记录已暂停" : "正在记录";

    public event EventHandler? CopyRequested;

    public event EventHandler? PasteRequested;

    public event EventHandler? QuickWindowRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? RecordingPauseToggleRequested;

    public event EventHandler? ExitRequested;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        if (_historyService is null)
        {
            RefreshDesignItems();
        }
        else if (Volatile.Read(ref _started) != 0)
        {
            ScheduleReload(debounce: true);
        }
    }

    partial void OnSelectedFilterChanged(ClipboardItemType? value)
    {
        OnPropertyChanged(nameof(IsAllFilterSelected));
        OnPropertyChanged(nameof(IsTextFilterSelected));
        OnPropertyChanged(nameof(IsImageFilterSelected));
        OnPropertyChanged(nameof(IsCodeFilterSelected));
        OnPropertyChanged(nameof(IsLinkFilterSelected));
        if (_historyService is null)
        {
            RefreshDesignItems();
        }
        else if (Volatile.Read(ref _started) != 0)
        {
            ScheduleReload(debounce: false);
        }
    }

    partial void OnSelectedItemChanged(ClipboardHistoryItemViewModel? value)
    {
        if (value is not null)
        {
            StatusMessage = $"已选择{value.KindLabel}记录";
            int index = VisibleItems.IndexOf(value);
            if (_historyService is not null && CanLoadMore && !IsLoading &&
                index >= Math.Max(0, VisibleItems.Count - 5))
            {
                _ = LoadMoreAsync();
            }
        }
    }

    partial void OnIsNewestFirstChanged(bool value)
    {
        OnPropertyChanged(nameof(SortLabel));
        if (_historyService is not null && Volatile.Read(ref _started) != 0)
        {
            ScheduleReload(debounce: false);
        }
    }

    partial void OnIsRecordingPausedChanged(bool value) =>
        OnPropertyChanged(nameof(RecordingStateText));

    partial void OnIsLoadingChanged(bool value) =>
        OnPropertyChanged(nameof(HasNoVisibleItems));

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
        if (_historyService is null)
        {
            RefreshDesignItems();
        }
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
    private async Task TogglePinAsync()
    {
        ClipboardHistoryItemViewModel? selected = SelectedItem;
        if (selected is null)
        {
            return;
        }

        bool nextValue = !selected.IsPinned;
        if (_historyService is not null &&
            !await _historyService.SetPinnedAsync(
                selected.Id,
                nextValue,
                _lifetime.Token))
        {
            StatusMessage = "置顶状态更新失败";
            return;
        }

        selected.IsPinned = nextValue;
        StatusMessage = nextValue ? "已置顶" : "已取消置顶";
    }

    [RelayCommand]
    private void Copy()
    {
        if (SelectedItem is not null)
        {
            StatusMessage = "已复制到剪贴板";
            CopyRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void Paste()
    {
        if (SelectedItem is not null)
        {
            StatusMessage = "已准备粘贴";
            PasteRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void OpenQuickWindow() => QuickWindowRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleRecordingPause() =>
        RecordingPauseToggleRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ExitApplication() => ExitRequested?.Invoke(this, EventArgs.Empty);

    internal void UpdateRecordingState(bool paused)
    {
        IsRecordingPaused = paused;
        StatusMessage = paused ? "剪贴板记录已暂停" : "剪贴板记录已恢复";
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
    private async Task DeleteAsync()
    {
        ClipboardHistoryItemViewModel? selected = SelectedItem;
        if (selected is null)
        {
            return;
        }

        int selectedIndex = VisibleItems.IndexOf(selected);
        if (_historyService is not null)
        {
            if (!await _historyService.DeleteAsync(selected.Id, _lifetime.Token))
            {
                StatusMessage = "记录移除失败";
                return;
            }

            VisibleItems.Remove(selected);
            selected.ReleaseResources();
            _totalCount = Math.Max(0, _totalCount - 1);
            OnPropertyChanged(nameof(RecordCountText));
            UpdateCollectionState();
        }
        else
        {
            _designItems.Remove(selected);
            RefreshDesignItems();
        }

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

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_historyService is null || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        SetPendingOperation(InitializeHistoryAsync());
    }

    public async ValueTask<ClipboardSelectedWriteRequest?> CreateSelectedWriteRequestAsync(
        bool plainText,
        CancellationToken cancellationToken)
    {
        ClipboardHistoryItemViewModel? selected = SelectedItem;
        if (selected is null)
        {
            return null;
        }

        if (_historyService is null)
        {
            return new ClipboardSelectedWriteRequest(
                selected.Id,
                new ClipboardWriteRequest { Text = selected.Content });
        }

        ClipboardHistoryContent? content = await _historyService
            .GetContentAsync(selected.Id, cancellationToken);
        if (content is null)
        {
            return null;
        }

        ClipboardWriteRequest request;
        if (plainText)
        {
            string text = content.Text ?? selected.Content;
            request = new ClipboardWriteRequest { Text = text };
        }
        else
        {
            request = new ClipboardWriteRequest
            {
                Text = content.Text,
                Html = content.Html,
                RichText = content.RichText,
                Bitmap = content.Bitmap is null ? null : MapBitmap(content.Bitmap),
                FilePaths = content.FilePaths,
            };
        }

        return request.HasContent
            ? new ClipboardSelectedWriteRequest(selected.Id, request)
            : null;
    }

    public async ValueTask RecordUseAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken)
    {
        if (_historyService is not null)
        {
            await _historyService.RecordUseAsync(
                itemId,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
    }

    public async Task LoadThumbnailAsync(ClipboardHistoryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_historyService is null || !item.HasThumbnail || item.Thumbnail is not null ||
            !item.TryBeginThumbnailLoad())
        {
            return;
        }

        Bitmap? decoded = null;
        try
        {
            ReadOnlyMemory<byte> thumbnail = await _historyService
                .GetThumbnailAsync(item.Id, _lifetime.Token);
            if (thumbnail.IsEmpty)
            {
                return;
            }

            byte[] bytes = thumbnail.ToArray();
            decoded = await Task.Run(
                () =>
                {
                    try
                    {
                        using MemoryStream stream = new(bytes, writable: false);
                        return new Bitmap(stream);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                },
                _lifetime.Token);

            if (VisibleItems.Contains(item) && Volatile.Read(ref _disposed) == 0)
            {
                item.Thumbnail = decoded;
                decoded = null;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // 缩略图失败只保留稳定占位图，不把文件路径或解码器异常暴露到 UI。
        }
        finally
        {
            decoded?.Dispose();
            if (item.Thumbnail is null)
            {
                item.ResetThumbnailLoad();
            }
        }
    }

    public async Task LoadSourceApplicationMetadataAsync(ClipboardHistoryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_sourceMetadataResolver is null || !item.TryBeginSourceMetadataLoad())
        {
            return;
        }

        Bitmap? iconBitmap = null;
        bool resolved = false;
        try
        {
            ClipboardSourceApplicationMetadata metadata = await _sourceMetadataResolver
                .ResolveAsync(
                    new ClipboardSourceApplicationIdentity(
                        item.SourceApplication,
                        item.SourceExecutablePath,
                        item.SourceApplicationUserModelId,
                        item.SourcePackageFamilyName),
                    _lifetime.Token);
            if (metadata.Icon is { } icon)
            {
                iconBitmap = CreateSourceIconBitmap(icon);
            }

            if (VisibleItems.Contains(item) && Volatile.Read(ref _disposed) == 0)
            {
                item.ApplySourceApplicationMetadata(metadata.DisplayName, iconBitmap);
                iconBitmap = null;
            }

            resolved = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // 来源路径和版本资源属于隐私信息；失败时保留进程名与通用图标，不透传细节。
        }
        finally
        {
            iconBitmap?.Dispose();
            if (!resolved)
            {
                item.ResetSourceMetadataLoad();
            }
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_historyService is null || IsLoading || _nextCursor is null ||
            _queryCancellation is null)
        {
            return;
        }

        Task operation = QueryPageAsync(
            reset: false,
            _nextCursor,
            Volatile.Read(ref _queryGeneration),
            debounce: false,
            _queryCancellation.Token);
        SetPendingOperation(operation);
        await operation;
    }

    internal async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task current = Volatile.Read(ref _pendingOperation);
            try
            {
                await current;
            }
            catch (OperationCanceledException)
            {
            }

            if (ReferenceEquals(current, Volatile.Read(ref _pendingOperation)))
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_historyService is not null)
        {
            _historyService.HistoryChanged -= OnHistoryChanged;
        }

        Timer? historyReloadTimer;
        lock (_historyReloadGate)
        {
            historyReloadTimer = _historyReloadTimer;
            _historyReloadTimer = null;
        }

        historyReloadTimer?.Dispose();

        _lifetime.Cancel();
        CancellationTokenSource? queryCancellation = Interlocked.Exchange(
            ref _queryCancellation,
            null);
        queryCancellation?.Cancel();
        queryCancellation?.Dispose();
        foreach (ClipboardHistoryItemViewModel item in VisibleItems)
        {
            item.ReleaseResources();
        }

        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task InitializeHistoryAsync()
    {
        try
        {
            ClipboardHistoryInitializationResult result = await _historyService!
                .InitializeAsync(_lifetime.Token);
            if (result.RecoveredCorruptDatabase)
            {
                StatusMessage = "历史数据库已诊断恢复，原文件已备份";
            }

            ScheduleReload(debounce: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // UI 只显示稳定诊断，不透传可能包含路径或 Provider 细节的异常文本。
            StatusMessage = "历史记录初始化失败";
        }
    }

    private void ScheduleReload(bool debounce)
    {
        if (_historyService is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CancellationTokenSource replacement = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _queryCancellation,
            replacement);
        previous?.Cancel();
        previous?.Dispose();

        long generation = Interlocked.Increment(ref _queryGeneration);
        _nextCursor = null;
        CanLoadMore = false;
        Task operation = QueryPageAsync(
            reset: true,
            cursor: null,
            generation,
            debounce,
            replacement.Token);
        SetPendingOperation(operation);
    }

    private async Task QueryPageAsync(
        bool reset,
        ClipboardHistoryCursor? cursor,
        long generation,
        bool debounce,
        CancellationToken cancellationToken)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(SearchDebounce, cancellationToken);
            }

            if (generation != Volatile.Read(ref _queryGeneration))
            {
                return;
            }

            IsLoading = true;
            ClipboardHistoryPage page = await _historyService!.SearchAsync(
                CreateQuery(cursor),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _queryGeneration))
            {
                return;
            }

            ApplyPage(page, reset);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (generation == Volatile.Read(ref _queryGeneration))
            {
                StatusMessage = "历史记录查询失败";
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _queryGeneration))
            {
                IsLoading = false;
            }
        }
    }

    private ClipboardHistoryQuery CreateQuery(ClipboardHistoryCursor? cursor) => new()
    {
        SearchText = SearchText,
        DisplayCategory = SelectedFilter switch
        {
            ClipboardItemType.Text => ClipboardHistoryDisplayCategory.Text,
            ClipboardItemType.Image => ClipboardHistoryDisplayCategory.Image,
            ClipboardItemType.Code => ClipboardHistoryDisplayCategory.Code,
            ClipboardItemType.Link => ClipboardHistoryDisplayCategory.Link,
            _ => null,
        },
        Cursor = cursor,
        PageSize = PageSize,
        NewestFirst = IsNewestFirst,
    };

    private void ApplyPage(ClipboardHistoryPage page, bool reset)
    {
        ClipboardItemId? selectedId = SelectedItem?.Id;
        if (reset)
        {
            foreach (ClipboardHistoryItemViewModel item in VisibleItems)
            {
                item.ReleaseResources();
            }

            VisibleItems.Clear();
        }

        HashSet<ClipboardItemId> existing = VisibleItems
            .Select(item => item.Id)
            .ToHashSet();
        foreach (ClipboardHistoryItemSummary item in page.Items)
        {
            if (existing.Add(item.Id))
            {
                VisibleItems.Add(new ClipboardHistoryItemViewModel(item));
            }
        }

        _nextCursor = page.NextCursor;
        CanLoadMore = _nextCursor is not null;
        _totalCount = page.TotalCount;
        SelectedItem = selectedId is { } identifier
            ? VisibleItems.FirstOrDefault(item => item.Id == identifier) ?? VisibleItems.FirstOrDefault()
            : VisibleItems.FirstOrDefault();
        StatusMessage = VisibleItems.Count == 0 ? "暂无剪贴板记录" : "就绪";
        OnPropertyChanged(nameof(RecordCountText));
        UpdateCollectionState();
    }

    private void UpdateCollectionState()
    {
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(HasNoVisibleItems));
        if (_totalCount < 0)
        {
            OnPropertyChanged(nameof(RecordCountText));
        }
    }

    private void OnHistoryChanged(object? sender, ClipboardHistoryChangedEvent e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_historyReloadGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Increment(ref _historyChangeVersion);
            _historyReloadDueAtMilliseconds = Environment.TickCount64 +
                (long)SearchDebounce.TotalMilliseconds;
            // 高频采集只重置同一个计时器，不为每个事件创建查询、CTS 或 UI 队列项。
            _historyReloadTimer ??= new Timer(
                static state => ((MainViewModel)state!).PostCoalescedHistoryReload(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _historyReloadTimer.Change(SearchDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void PostCoalescedHistoryReload()
    {
        long version;
        lock (_historyReloadGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            long remainingMilliseconds = _historyReloadDueAtMilliseconds -
                Environment.TickCount64;
            if (remainingMilliseconds > 0)
            {
                // Change 不能撤回已经排队的回调；旧回调只负责重新对齐最新静默期。
                _historyReloadTimer?.Change(
                    TimeSpan.FromMilliseconds(remainingMilliseconds),
                    Timeout.InfiniteTimeSpan);
                return;
            }

            version = Volatile.Read(ref _historyChangeVersion);
        }

        PostToUi(() =>
        {
            // 计时器触发后若又有新记录，后续重置的计时器负责刷新，旧回调不读取数据库。
            if (version == Volatile.Read(ref _historyChangeVersion))
            {
                ScheduleReload(debounce: false);
            }
        });
    }

    private void PostToUi(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (ReferenceEquals(SynchronizationContext.Current, _uiContext) ||
            (_uiContext is null && Dispatcher.UIThread.CheckAccess()))
        {
            action();
            return;
        }

        if (_uiContext is not null)
        {
            _uiContext.Post(
                static state =>
                {
                    var (owner, callback) = ((MainViewModel, Action))state!;
                    if (Volatile.Read(ref owner._disposed) == 0)
                    {
                        callback();
                    }
                },
                (this, action));
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                action();
            }
        });
    }

    private void SetPendingOperation(Task operation) =>
        Volatile.Write(ref _pendingOperation, operation);

    private static ClipboardBitmapData MapBitmap(ClipboardHistoryBitmap bitmap) => new(
        bitmap.Encoding switch
        {
            ClipboardStoredBitmapEncoding.DeviceIndependentBitmap =>
                ClipboardBitmapEncoding.DeviceIndependentBitmap,
            ClipboardStoredBitmapEncoding.DeviceIndependentBitmapV5 =>
                ClipboardBitmapEncoding.DeviceIndependentBitmapV5,
            ClipboardStoredBitmapEncoding.PortableNetworkGraphics =>
                ClipboardBitmapEncoding.PortableNetworkGraphics,
            ClipboardStoredBitmapEncoding.TaggedImageFileFormat =>
                ClipboardBitmapEncoding.TaggedImageFileFormat,
            _ => throw new ArgumentOutOfRangeException(nameof(bitmap)),
        },
        bitmap.Data,
        bitmap.Width,
        bitmap.Height,
        bitmap.BitsPerPixel);

    private static WriteableBitmap? CreateSourceIconBitmap(ClipboardSourceApplicationIcon icon)
    {
        int rowBytes;
        int requiredBytes;
        try
        {
            rowBytes = checked(icon.Width * 4);
            requiredBytes = checked(icon.Stride * icon.Height);
        }
        catch (OverflowException)
        {
            return null;
        }

        if (icon.Width is < 1 or > 256 || icon.Height is < 1 or > 256 ||
            icon.Stride < rowBytes || icon.BgraPixels.Length < requiredBytes)
        {
            return null;
        }

        WriteableBitmap bitmap = new(
            new PixelSize(icon.Width, icon.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        try
        {
            byte[] pixels = icon.BgraPixels.ToArray();
            using var framebuffer = bitmap.Lock();
            for (int row = 0; row < icon.Height; row++)
            {
                Marshal.Copy(
                    pixels,
                    row * icon.Stride,
                    nint.Add(framebuffer.Address, row * framebuffer.RowBytes),
                    rowBytes);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private void RefreshDesignItems()
    {
        IEnumerable<ClipboardHistoryItemViewModel> query = _designItems;

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
        _totalCount = VisibleItems.Count;
        OnPropertyChanged(nameof(RecordCountText));
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
