using System.Reflection;
using SnapBoard.Desktop.ViewModels;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class MainViewModelTests
{
    [Fact]
    public void DesktopAssemblyUsesChineseDisplayMetadataAndStableInternalName()
    {
        Assembly assembly = typeof(App).Assembly;

        Assert.Equal("SnapBoard.Desktop", assembly.GetName().Name);
        Assert.Equal("闪剪", assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title);
        Assert.Equal("闪剪", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
        Assert.Equal("闪剪", assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description);
    }

    [Fact]
    public void NewViewModelStartsWithPopulatedCommandCenterState()
    {
        MainViewModel viewModel = new();

        Assert.Empty(viewModel.SearchText);
        Assert.Equal("闪剪", viewModel.ProductName);
        Assert.Equal(10, viewModel.VisibleItems.Count);
        Assert.NotNull(viewModel.SelectedItem);
        Assert.True(viewModel.IsAllFilterSelected);
    }

    [Fact]
    public void SearchFiltersAcrossContentAndSourceApplication()
    {
        MainViewModel viewModel = new();

        viewModel.SearchText = "Avalonia";

        Assert.Equal(2, viewModel.VisibleItems.Count);
        Assert.All(viewModel.VisibleItems, item =>
            Assert.Contains("Avalonia", $"{item.Title} {item.Subtitle} {item.Content}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypeFilterKeepsOnlyMatchingRecords()
    {
        MainViewModel viewModel = new();

        viewModel.SelectFilterCommand.Execute("Link");

        Assert.True(viewModel.IsLinkFilterSelected);
        Assert.Equal(2, viewModel.VisibleItems.Count);
        Assert.All(viewModel.VisibleItems, item => Assert.Equal(ClipboardItemType.Link, item.Type));
    }

    [Fact]
    public void CompactModeCollapsesPreviewColumn()
    {
        MainViewModel viewModel = new();

        viewModel.ToggleCompactModeCommand.Execute(null);

        Assert.True(viewModel.IsCompactMode);
        Assert.Equal(0, viewModel.PreviewColumnWidth.Value);
        Assert.True(viewModel.HistoryColumnWidth.IsStar);
    }

    [Fact]
    public void DeleteRemovesSelectedRecordAndSelectsAnother()
    {
        MainViewModel viewModel = new();
        ClipboardHistoryItemViewModel selected = Assert.IsType<ClipboardHistoryItemViewModel>(viewModel.SelectedItem);

        viewModel.DeleteCommand.Execute(null);

        Assert.DoesNotContain(selected, viewModel.VisibleItems);
        Assert.Equal(9, viewModel.VisibleItems.Count);
        Assert.NotNull(viewModel.SelectedItem);
    }
}
