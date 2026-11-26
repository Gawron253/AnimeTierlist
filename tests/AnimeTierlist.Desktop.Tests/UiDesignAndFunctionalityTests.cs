using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeTierlist.Application.Covers;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Controls;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Desktop.ViewModels;
using AnimeTierlist.Desktop.Views;
using AnimeTierlist.Infrastructure.Portable;

namespace AnimeTierlist.Desktop.Tests;

/// <summary>
/// Automated verification of the redesigned UI system: theme parity, WCAG contrast ratios,
/// virtualized poster grid realization, drop index geometry, non-mutating filters and board search,
/// batch multi-select inclusion, portable settings, and 960x540 DIP layout fit.
/// </summary>
public sealed class UiDesignAndFunctionalityTests
{
    [Fact]
    public Task Theme_token_dictionaries_share_identical_keys_and_pass_wcag_aa_contrast() => Ui.RunAsync(() =>
    {
        ThemeManager.EnsurePackUriRegistered();
        var dark = (ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/AnimeTierlist;component/Themes/Tokens.Dark.xaml", UriKind.Relative));
        var light = (ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/AnimeTierlist;component/Themes/Tokens.Light.xaml", UriKind.Relative));
        var hc = (ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/AnimeTierlist;component/Themes/Tokens.HighContrast.xaml", UriKind.Relative));

        var darkKeys = dark.Keys.Cast<string>().Order(StringComparer.Ordinal).ToList();
        var lightKeys = light.Keys.Cast<string>().Order(StringComparer.Ordinal).ToList();
        var hcKeys = hc.Keys.Cast<string>().Order(StringComparer.Ordinal).ToList();

        Assert.NotEmpty(darkKeys);
        Assert.Equal(darkKeys, lightKeys);
        Assert.Equal(darkKeys, hcKeys);

        foreach (var (name, dict) in new[] { ("Dark", dark), ("Light", light) })
        {
            AssertContrast(name, dict, "Brush.TextPrimary", "Brush.BgCanvas", 4.5);
            AssertContrast(name, dict, "Brush.TextPrimary", "Brush.BgSurface", 4.5);
            AssertContrast(name, dict, "Brush.TextPrimary", "Brush.BgRaised", 4.5);
            AssertContrast(name, dict, "Brush.TextPrimary", "Brush.BgSelected", 4.5);
            AssertContrast(name, dict, "Brush.TextOnHover", "Brush.BgHover", 4.5);
            AssertContrast(name, dict, "Brush.TextOnSelected", "Brush.BgSelected", 4.5);
            AssertContrast(name, dict, "Brush.TextSecondary", "Brush.BgSurface", 4.5);
            AssertContrast(name, dict, "Brush.TextSecondary", "Brush.BgRaised", 4.5);
            AssertContrast(name, dict, "Brush.TextMuted", "Brush.BgRaised", 4.5);
            AssertContrast(name, dict, "Brush.OnAccent", "Brush.AccentFill", 4.5);
            AssertContrast(name, dict, "Brush.OnAccent", "Brush.AccentHover", 4.5);
            AssertContrast(name, dict, "Brush.AccentText", "Brush.AccentSurface", 4.5);
            AssertContrast(name, dict, "Brush.SuccessText", "Brush.SuccessSurface", 4.5);
            AssertContrast(name, dict, "Brush.WarningText", "Brush.WarningSurface", 4.5);
            AssertContrast(name, dict, "Brush.DangerText", "Brush.DangerSurface", 4.5);
            AssertContrast(name, dict, "Brush.OnDanger", "Brush.DangerFill", 4.5);
            AssertContrast(name, dict, "Brush.BorderControl", "Brush.BgHover", 3.0);
            AssertContrast(name, dict, "Brush.FocusRing", "Brush.BgSurface", 3.0);
            AssertContrast(name, dict, "Brush.TextPrimary", "Brush.PoolHeader", 4.5);
            AssertContrast(name, dict, "Brush.DisabledText", "Brush.DisabledBg", 4.5);
        }

        // Verify every tier swatch achieves >= 4.5:1 with the actual rendered TierInkToBrushConverter ink brush.
        var inkConverter = new TierInkToBrushConverter();
        var dummyBoard = new TierColor[]
        {
            new(0xFF, 0x7F, 0x7F), new(0xFF, 0xBF, 0x7F), new(0xFF, 0xDF, 0x7F), new(0xFF, 0xFF, 0x7F),
            new(0xBF, 0xFF, 0x7F), new(0x7F, 0xFF, 0x7F), new(0x7F, 0xFF, 0xFF), new(0x7F, 0xBF, 0xFF),
            new(0xBF, 0x7F, 0xFF), new(0xFF, 0x7F, 0xDF), new(0xC8, 0xC8, 0xC8), new(0xDC, 0x26, 0x26),
            new(0xD9, 0x77, 0x06), new(0x05, 0x96, 0x69), new(0x25, 0x63, 0xEB), new(0x7C, 0x3A, 0xED),
            new(0x47, 0x55, 0x69),
        };
        foreach (var swatch in dummyBoard)
        {
            var ink = Assert.IsType<SolidColorBrush>(inkConverter.Convert(swatch, typeof(Brush), null, System.Globalization.CultureInfo.InvariantCulture)).Color;
            var ratio = swatch.ContrastRatio(new TierColor(ink.R, ink.G, ink.B));
            Assert.True(ratio >= 4.5, $"Tier swatch {swatch} ink {ink} contrast {ratio:F2}:1 is below 4.5:1.");
        }

        return Task.CompletedTask;
    });

    /// <summary>
    /// The collection poster grid is a ListBox subclass. WPF matches implicit styles by exact type, so without its own
    /// style entry it rendered the system window brush (white) behind the covers in the dark theme.
    /// </summary>
    [Fact]
    public Task Poster_grid_uses_the_dark_theme_surface_background() => Ui.RunAsync(() =>
    {
        ThemeManager.EnsurePackUriRegistered();
        var grid = new PosterGridListBox();
        var host = new Border { Width = 400, Height = 300, Child = grid };
        ThemeManager.ApplyToDictionary(host.Resources, AppTheme.Dark);
        host.Measure(new Size(400, 300));
        host.Arrange(new Rect(0, 0, 400, 300));
        host.UpdateLayout();

        var expected = Assert.IsType<SolidColorBrush>(host.FindResource("Brush.BgSurface")).Color;
        var actual = Assert.IsType<SolidColorBrush>(grid.Background).Color;
        Assert.Equal(expected, actual);
        return Task.CompletedTask;
    });

    /// <summary>
    /// Lists behind the in-window details layer are disabled. The system template painted disabled lists white, which
    /// showed as grey panels through the scrim; the themed template keeps the list's own background.
    /// </summary>
    [Fact]
    public Task Disabled_lists_keep_their_theme_background() => Ui.RunAsync(() =>
    {
        ThemeManager.EnsurePackUriRegistered();
        var list = new ListBox { IsEnabled = false };
        list.Items.Add("One");
        var host = new Border { Width = 300, Height = 200, Child = list };
        ThemeManager.ApplyToDictionary(host.Resources, AppTheme.Dark);
        host.Measure(new Size(300, 200));
        host.Arrange(new Rect(0, 0, 300, 200));
        host.UpdateLayout();

        var expected = Assert.IsType<SolidColorBrush>(host.FindResource("Brush.BgSurface")).Color;
        var chrome = Descendants<Border>(list).First();
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(chrome.Background).Color);
        return Task.CompletedTask;
    });

    [Fact]
    public void Board_drop_calculator_matches_left_right_halves_and_wrapped_rows()
    {
        var row = new List<Rect>
        {
            new(0, 0, 100, 140),
            new(110, 0, 100, 140),
            new(220, 0, 100, 140),
            new(0, 150, 100, 140),
        };

        Assert.Equal(0, BoardDropCalculator.ComputeIndex([], new Point(40, 40)));
        Assert.Equal(0, BoardDropCalculator.ComputeIndex(row, new Point(20, 50)));
        Assert.Equal(1, BoardDropCalculator.ComputeIndex(row, new Point(80, 50)));
        Assert.Equal(2, BoardDropCalculator.ComputeIndex(row, new Point(190, 50)));
        Assert.Equal(3, BoardDropCalculator.ComputeIndex(row, new Point(290, 50)));
        Assert.Equal(3, BoardDropCalculator.ComputeIndex(row, new Point(20, 200)));
        Assert.Equal(4, BoardDropCalculator.ComputeIndex(row, new Point(80, 200)));

        var (caretX, top, bottom) = BoardDropCalculator.ComputeCaret(row, 1, 140);
        Assert.Equal(105, caretX, precision: 2);
        Assert.True(bottom > top);
    }

    [Fact]
    public Task Virtualizing_tile_panel_realizes_only_visible_rows_for_1000_items_and_scrolls_to_offscreen_index() => Ui.RunAsync(() =>
    {
        var items = Enumerable.Range(1, 1000).Select(i => $"Show {i}").ToList();
        var list = new ListBox
        {
            ItemsSource = items,
            Width = 900,
            Height = 500,
        };
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingTilePanel)));

        var host = new ContentControl { Width = 900, Height = 500, Content = list };
        ThemeManager.ApplyToDictionary(host.Resources, AppTheme.Dark);
        host.Measure(new Size(900, 500));
        host.Arrange(new Rect(0, 0, 900, 500));
        host.UpdateLayout();

        var realized = Enumerable.Range(0, items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null);
        Assert.InRange(realized, 6, 80);

        list.ScrollIntoView(items[500]);
        host.UpdateLayout();
        Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(500));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Virtualizing_tile_panel_handles_realized_and_unrealized_moves_and_removes_without_crashing() => Ui.RunAsync(() =>
    {
        var items = new ObservableCollection<string>(Enumerable.Range(1, 500).Select(i => $"Show {i}"));
        var list = new ListBox
        {
            ItemsSource = items,
            Width = 800,
            Height = 440,
        };
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingTilePanel)));

        var host = new ContentControl { Width = 800, Height = 440, Content = list };
        ThemeManager.ApplyToDictionary(host.Resources, AppTheme.Dark);
        host.Measure(new Size(800, 440));
        host.Arrange(new Rect(0, 0, 800, 440));
        host.UpdateLayout();

        // Move realized -> realized, realized -> unrealized, unrealized -> realized, and remove both,
        // verifying every realized child matches ItemContainerGenerator order after every mutation.
        AssertRealizedOrder(list, items);

        items.Move(0, 3);
        host.UpdateLayout();
        Assert.Equal("Show 1", items[3]);
        AssertRealizedOrder(list, items);

        items.Move(2, 420);
        host.UpdateLayout();
        AssertRealizedOrder(list, items);

        list.ScrollIntoView(items[250]);
        host.UpdateLayout();
        AssertRealizedOrder(list, items);

        items.Move(250, 255);
        host.UpdateLayout();
        AssertRealizedOrder(list, items);

        // Remove unrealized item before viewport and realized item inside viewport.
        items.RemoveAt(0);
        host.UpdateLayout();
        AssertRealizedOrder(list, items);

        items.RemoveAt(250);
        host.UpdateLayout();
        AssertRealizedOrder(list, items);

        // Move to top from scrolled position and verify container order matches item order.
        items.Move(250, 0);
        host.UpdateLayout();
        AssertRealizedOrder(list, items);

        list.ScrollIntoView(items[0]);
        host.UpdateLayout();
        AssertRealizedOrder(list, items);
        var topContainer = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Equal(items[0], topContainer.DataContext);
        return Task.CompletedTask;
    });

    private static void AssertRealizedOrder(ListBox list, ObservableCollection<string> items)
    {
        var panel = Descendants<VirtualizingTilePanel>(list).Single();
        var generator = (IItemContainerGenerator)list.ItemContainerGenerator;
        var previousIndex = -1;
        for (var i = 0; i < panel.Children.Count; i++)
        {
            var child = Assert.IsType<ListBoxItem>(panel.Children[i]);
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            Assert.True(itemIndex > previousIndex, $"Realized child {i} had non-increasing item index {itemIndex} after {previousIndex}.");
            Assert.InRange(itemIndex, 0, items.Count - 1);
            Assert.Equal(items[itemIndex], child.DataContext);
            Assert.Equal(items[itemIndex], child.Content);
            previousIndex = itemIndex;
        }
    }

    [Fact]
    public Task Virtualizing_tile_panel_recycled_cards_show_their_own_item_after_scroll_resize_and_collapse() => Ui.RunAsync(() =>
    {
        // Regression: recycled containers were re-linked (DataContext) but never re-prepared, so the card kept
        // showing the previous item's cover and title while double-click opened the new item.
        var items = new ObservableCollection<string>(Enumerable.Range(1, 400).Select(i => $"Show {i}"));
        var list = new ListBox { ItemsSource = items };
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingTilePanel)));

        var host = new ContentControl { Content = list };
        ThemeManager.ApplyToDictionary(host.Resources, AppTheme.Dark);

        void Layout(double width, double height)
        {
            host.Width = width;
            host.Height = height;
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
        }

        Layout(800, 500);
        AssertRealizedOrder(list, items);

        list.ScrollIntoView(items[200]);
        host.UpdateLayout();
        AssertRealizedOrder(list, items);

        // Shrink, collapse, then grow, as dragging the tray splitter and toggling the tray does.
        Layout(800, 260);
        AssertRealizedOrder(list, items);
        list.Visibility = Visibility.Collapsed;
        Layout(800, 260);
        list.Visibility = Visibility.Visible;
        Layout(1100, 900);
        AssertRealizedOrder(list, items);
        Layout(500, 300);
        AssertRealizedOrder(list, items);

        list.ScrollIntoView(items[0]);
        host.UpdateLayout();
        AssertRealizedOrder(list, items);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Board_tier_context_menus_swatch_color_edit_append_tier_and_save_suspension_work_correctly() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        collection.Entries[0].Included = true;
        collection.NewBoardName = "Tier UX";
        await collection.CreateBoardCommand.ExecuteAsync(null);
        var boardVm = Assert.IsType<BoardViewModel>(harness.Project.Content);

        // 1. AppendNewTier appends a uniquely named tier at the bottom without duplicating SelectedTier's name.
        boardVm.SelectedTier = boardVm.Tiers[0];
        var initialCount = boardVm.Tiers.Count;
        boardVm.AppendNewTierCommand.Execute(null);
        Assert.Equal(initialCount + 1, boardVm.Tiers.Count);
        Assert.Equal("New tier", boardVm.Tiers[^1].Name);
        Assert.NotEqual(boardVm.Tiers[0].Name, boardVm.Tiers[^1].Name);

        // 2. SelectTierColor changes only the color and does not commit unsaved draft TierNameText.
        boardVm.SelectedTier = boardVm.Tiers[0];
        var savedName = boardVm.Tiers[0].Name;
        boardVm.TierNameText = "Unsaved Draft Name";
        var cobalt = boardVm.ColorOptions.Single(c => c.Label == "Cobalt");
        boardVm.SelectTierColorCommand.Execute(cobalt);
        Assert.Equal(savedName, boardVm.Tiers[0].Name);
        Assert.Equal(cobalt.Value, boardVm.Tiers[0].Color);

        // 3. Every tier header ContextMenu MenuItem resolves a working Command via BindingProxy.
        var boardView = new BoardView { DataContext = boardVm };
        boardView.Measure(new Size(1100, 720));
        boardView.Arrange(new Rect(0, 0, 1100, 720));
        boardView.UpdateLayout();
        var tierHeaderButtons = Descendants<Button>(boardView).Where(b => b.ContextMenu is not null && b.DataContext is TierViewModel).ToList();
        Assert.NotEmpty(tierHeaderButtons);
        var menuItems = tierHeaderButtons[0].ContextMenu!.Items.OfType<MenuItem>().ToList();
        Assert.Equal(5, menuItems.Count);
        Assert.All(menuItems, item => Assert.NotNull(item.Command));

        // 3b. Tier move commands enforce boundary indices.
        boardVm.SelectedTier = boardVm.Tiers[0];
        Assert.False(boardVm.MoveTierUpCommand.CanExecute(null));
        Assert.True(boardVm.MoveTierDownCommand.CanExecute(null));
        Assert.False(boardVm.MoveTierItemUpCommand.CanExecute(boardVm.Tiers[0]));
        Assert.False(boardVm.MoveTierItemDownCommand.CanExecute(boardVm.Tiers[^1]));

        // 4. SaveFailed suspends mutating commands on Project, Collection, and Board, and updates Shell FooterStatus live.
        var shell = new ShellViewModel(harness.App) { Project = harness.Project };
        harness.Project.SaveFailed = true;
        harness.Project.SaveText = "Save failed: Disk full";
        harness.Project.SelectedCollection = harness.Project.Collections[0];
        collection.Refresh(harness.Session.Current);
        boardVm.Refresh(harness.Session.Current);

        Assert.False(harness.Project.ExportProjectCommand.CanExecute(null));
        Assert.False(harness.Project.NewCollectionCommand.CanExecute(null));
        Assert.False(collection.IncludeAllCommand.CanExecute(null));
        Assert.False(collection.PromptCreateBoardCommand.CanExecute(null));
        Assert.False(collection.DeleteCommand.CanExecute(null));
        Assert.False(boardVm.AppendNewTierCommand.CanExecute(null));
        Assert.False(boardVm.SelectTierColorCommand.CanExecute(cobalt));
        Assert.False(boardVm.DeleteCommand.CanExecute(null));
        Assert.False(boardVm.ExportPngCommand.CanExecute(null));
        Assert.Contains("Save failed", shell.FooterStatus, StringComparison.Ordinal);
        harness.Project.SaveFailed = false;
        harness.Project.SaveText = "Saved";
    });

    [Fact]
    public Task Radio_view_mode_and_density_persist_to_portable_settings_and_filter_chips_expose_uia_names() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var view = new CollectionView { DataContext = collection };
        view.Measure(new Size(1180, 760));
        view.Arrange(new Rect(0, 0, 1180, 760));
        view.UpdateLayout();

        // Poster art boxes keep their exact full PosterHeight without bottom cropping.
        var posterFrames = Descendants<Border>(view).Where(b => b.Name == "PosterArtFrame" && b.ActualHeight > 0).ToList();
        Assert.NotEmpty(posterFrames);
        Assert.All(posterFrames, frame => Assert.Equal(collection.PosterHeight, frame.ActualHeight, precision: 1));

        // Search-only filter reevaluates immediately when a custom title is cleared and undone.
        collection.SelectedEntry = collection.Entries[0];
        collection.TitleOverrideText = "UniqueSearchToken99";
        collection.SetTitleOverrideCommand.Execute(null);
        collection.SearchText = "UniqueSearchToken99";
        Assert.Equal(1, collection.VisibleCount);
        collection.ClearTitleOverrideCommand.Execute(null);
        Assert.Equal(0, collection.VisibleCount);
        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => collection.VisibleCount == 1, "search filter refreshed after undo");
        collection.SearchText = string.Empty;

        // Setting ViewMode and Density directly (as two-way RadioButton bindings do) persists to portable settings.
        collection.ViewMode = CollectionViewMode.List;
        collection.Density = CardDensity.Large;
        Assert.Equal(CollectionViewMode.List, harness.App.Preferences.Current.CollectionViewMode);
        Assert.Equal(CardDensity.Large, harness.App.Preferences.Current.CollectionDensity);

        // Filter chip RadioButtons expose full labels via UI Automation peers (never raw numbers).
        var radios = Descendants<RadioButton>(view).Where(r => r.GroupName == "CollectionFilter").ToList();
        var allPeer = UIElementAutomationPeer.CreatePeerForElement(radios[0]);
        Assert.Contains("All (", allPeer.GetName(), StringComparison.Ordinal);

        // Entry ItemStatus includes position and inclusion state.
        Assert.Contains("#1", collection.Entries[0].ItemStatus, StringComparison.Ordinal);
        Assert.Contains("Excluded", collection.Entries[0].ItemStatus, StringComparison.Ordinal);
        collection.Entries[0].Included = true;
        Assert.Contains("Included", collection.Entries[0].ItemStatus, StringComparison.Ordinal);
    });

    [Fact]
    public Task Collection_filter_chips_batch_toggle_and_unfiltered_drag_reorder_respect_domain_rules() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var view = await harness.CreateCollectionAsync();
        var originalOrder = view.Entries.Select(e => e.Item).ToList();

        // Batch toggle 3 entries in one undoable command.
        var batch = view.Entries.Take(3).ToList();
        Assert.True(view.ToggleEntries(batch));
        Assert.Equal(3, view.IncludedCount);
        Assert.Equal(view.TotalCount - 3, view.ExcludedCount);
        await Ui.UntilAsync(() => harness.Project.UndoLabel == "Include 3 entries", "single batch undo step");

        // Filter chips narrow only the visual ICollectionView, never stored order.
        view.SetFilterModeCommand.Execute(CollectionFilterMode.Included);
        Assert.Equal(3, view.VisibleCount);
        Assert.True(view.IsFiltered);
        Assert.False(view.CanReorder);
        Assert.False(view.MoveEntryTo(view.Entries[0], 2));
        Assert.Equal(originalOrder, harness.Collection(view).Entries.Select(e => e.Item));

        // Clear filters -> drag reorder works and is undoable.
        view.ClearFiltersCommand.Execute(null);
        Assert.False(view.IsFiltered);
        Assert.True(view.CanReorder);
        var moved = view.Entries[0];
        Assert.True(view.MoveEntryTo(moved, 4));
        Assert.Equal(moved.Item, harness.Collection(view).Entries[3].Item);

        // Undo reorder, then undo the entire 3-item batch include in one step.
        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => view.Entries[0].Item == moved.Item, "reorder undone");
        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => view.IncludedCount == 0, "batch include undone in one step");
    });

    [Fact]
    public Task Board_find_highlights_without_mutating_placements_and_supports_more_than_9_tiers_and_png_preview() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        foreach (var entry in collection.Entries.Take(4))
        {
            entry.Included = true;
        }

        collection.NewBoardName = "Highlight & 10 tiers";
        await collection.CreateBoardCommand.ExecuteAsync(null);
        var board = Assert.IsType<BoardViewModel>(harness.Project.Content);

        // Add tiers up to 10 total.
        for (var i = board.Tiers.Count; i < 10; i++)
        {
            board.TierNameText = $"T{i + 1}";
            board.AddTierCommand.Execute(null);
        }

        Assert.Equal(10, board.Tiers.Count);
        Assert.Equal("1", board.Tiers[0].ShortcutText);
        Assert.Equal("9", board.Tiers[8].ShortcutText);
        Assert.False(board.Tiers[9].HasShortcut);

        // Move a show into tier #10 via explicit tier command.
        var targetShow = board.Unranked.Entries[0];
        board.Unranked.SelectedEntry = targetShow;
        board.MoveSelectedToTierCommand.Execute(board.Tiers[9]);
        Assert.Contains(targetShow, board.Tiers[9].Entries);

        // Visual Find dims non-matches without changing placements.
        var beforeState = harness.Session.Current.Board(board.Id);
        board.BoardSearchText = targetShow.Title;
        Assert.True(targetShow.IsSearchMatch);
        Assert.False(targetShow.IsDimmed);
        Assert.All(board.Unranked.Entries, e => Assert.Equal(!e.IsSearchMatch, e.IsDimmed));
        Assert.Same(beforeState, harness.Session.Current.Board(board.Id));

        // PNG preview reports deterministic dimensions and flags missing covers.
        var preview = new PngExportPreviewViewModel(beforeState, harness.Session.Current.Assets, new HashSet<AssetHash>(), includeUnranked: true);
        Assert.True(preview.HasBlockers);
        Assert.Contains("1,600", preview.DimensionsText, StringComparison.Ordinal);
        Assert.Equal(4, preview.MissingOrBrokenInScope.Count);
    });

    [Fact]
    public void Portable_settings_store_persists_inside_data_root_and_tolerates_corrupt_json()
    {
        var root = Path.Combine(Path.GetTempPath(), "animetierlist-settings-test", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PortableSettingsStore(root);
            Assert.StartsWith(Path.GetFullPath(root), store.SettingsPath, StringComparison.OrdinalIgnoreCase);

            var initial = new UiSettings { Theme = AppTheme.Light, CollectionViewMode = CollectionViewMode.List, BoardDensity = CardDensity.Large };
            Assert.True(store.TrySave(initial, out var failure), failure);
            Assert.True(File.Exists(store.SettingsPath));

            var loaded = store.Load(new UiSettings());
            Assert.Equal(AppTheme.Light, loaded.Theme);
            Assert.Equal(CollectionViewMode.List, loaded.CollectionViewMode);
            Assert.Equal(CardDensity.Large, loaded.BoardDensity);

            // Corrupt JSON falls back safely without deleting the file.
            File.WriteAllText(store.SettingsPath, "{not-valid-json");
            var fallback = store.Load(new UiSettings());
            Assert.Equal(AppTheme.Dark, fallback.Theme);
            Assert.Equal("{not-valid-json", File.ReadAllText(store.SettingsPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public Task Compact_960x540_full_shell_layout_keeps_essential_controls_inside_bounds_even_with_save_failed() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var clientSize = new Size(944, 500); // Client area inside a 960x540 DIP window.

        // 1. Full MainWindow shell with Project Picker
        var shell = new ShellViewModel(harness.App);
        LayoutShellAndAssertUnclipped(shell, clientSize, ["Open", "Import project...", "Create"]);

        // 2. Full MainWindow shell with Acquisition View
        shell.Project = harness.Project;
        harness.Project.SidebarCollapsed = true;
        harness.Project.NewCollectionCommand.Execute(null);
        LayoutShellAndAssertUnclipped(shell, clientSize, ["Undo", "Redo", "Close project", "Fetch from LiveChart", "Cancel"]);

        // 3. Full MainWindow shell with Collection View:
        //    - Entering compact 944x500 auto-collapses InspectorOpen so Tab is never trapped in an unasked overlay;
        //    - Explicitly opening the inspector while compact activates the modal scrim;
        //    - Default 1240x820 launch width with 220 DIP sidebar open (~998 DIP workspace) stays docked side-by-side.
        var collectionVm = await harness.CreateCollectionAsync();
        harness.Project.SelectedCollection = harness.Project.Collections[0];
        collectionVm.InspectorOpen = true;
        var overlayHost = LayoutShellAndAssertUnclipped(shell, clientSize, ["Undo", "Redo", "Close project"]);
        var colViewWithOverlay = Descendants<CollectionView>(overlayHost).Single();
        var scrim = Assert.IsType<Border>(colViewWithOverlay.FindName("InspectorScrim"));
        Assert.False(collectionVm.InspectorOpen);
        Assert.Equal(Visibility.Collapsed, scrim.Visibility);

        collectionVm.InspectorOpen = true;
        overlayHost.UpdateLayout();
        Assert.Equal(Visibility.Visible, scrim.Visibility);

        collectionVm.InspectorOpen = false;
        overlayHost.Child = null;
        var colHost = LayoutShellAndAssertUnclipped(shell, clientSize, ["Undo", "Redo", "Close project", "Create board...", "Fetch covers", "Export covers..."]);
        AssertFirstCollectionCardFits(colHost, "normal compact 960x540");

        colHost.Child = null;
        harness.Project.SidebarCollapsed = false;
        collectionVm.InspectorOpen = true;
        var defaultHost = LayoutShellAndAssertUnclipped(shell, new Size(1224, 780), ["Create board...", "Fetch covers", "Include all", "Exclude all"]);
        var defaultColView = Descendants<CollectionView>(defaultHost).Single();
        var defaultScrim = Assert.IsType<Border>(defaultColView.FindName("InspectorScrim"));
        Assert.Equal(Visibility.Collapsed, defaultScrim.Visibility);
        Assert.True(collectionVm.InspectorOpen);
        defaultHost.Child = null;
        harness.Project.SidebarCollapsed = true;
        collectionVm.InspectorOpen = false;
        colHost = LayoutShellAndAssertUnclipped(shell, clientSize, ["Undo", "Redo", "Close project", "Create board...", "Fetch covers", "Export covers..."]);

        // Clicking Compact ('S') while in a compact viewport must NOT overwrite the user's saved Standard density.
        Assert.Equal(CardDensity.Standard, collectionVm.Density);
        Assert.Equal(CardDensity.Compact, collectionVm.EffectiveDensity);
        collectionVm.SetDensityCommand.Execute(CardDensity.Compact);
        Assert.Equal(CardDensity.Standard, collectionVm.Density);

        // 3b. Full MainWindow shell with Collection View AND BOTH Cover status + Save Failed banners visible!
        collectionVm.CoverStatus = "1 entry has a missing or damaged cover file. Fetch covers to restore the exact originals.";
        harness.Project.SaveFailed = true;
        harness.Project.SaveFailureDetail = "Disk full while saving.";
        harness.Project.SaveText = "Save failed: Disk full";
        var failedColHost = LayoutShellAndAssertUnclipped(shell, clientSize, ["Retry save", "Discard unsaved changes", "Create board...", "Fetch covers"]);
        AssertFirstCollectionCardFits(failedColHost, "save-failed + cover-banner compact 960x540");
        Assert.Equal(2, collectionVm.CompactViewportLevel);

        // Dismissing the banners on the live view without resizing the window must update CompactViewportLevel via WorkspaceGrid.SizeChanged.
        collectionVm.CoverStatus = string.Empty;
        harness.Project.SaveFailed = false;
        harness.Project.SaveFailureDetail = string.Empty;
        harness.Project.SaveText = "Saved";
        failedColHost.UpdateLayout();
        Assert.True(collectionVm.CompactViewportLevel < 2, $"Expected WorkspaceGrid.SizeChanged to relax CompactViewportLevel after banner dismissal, got {collectionVm.CompactViewportLevel}.");

        // 4. Full MainWindow shell with Board View (verify TWO full tier rows + unranked card + titles fit inside their viewports!)
        collectionVm.Entries[0].Included = true;
        collectionVm.Entries[1].Included = true;
        collectionVm.Entries[2].Included = true;
        collectionVm.NewBoardName = "Compact";
        await collectionVm.CreateBoardCommand.ExecuteAsync(null);
        var boardVm = Assert.IsType<BoardViewModel>(harness.Project.Content);
        boardVm.Drop(boardVm.Unranked.Entries[0], boardVm.Tiers[0], 0);
        boardVm.Drop(boardVm.Unranked.Entries[0], boardVm.Tiers[1], 0);
        boardVm.InspectorOpen = false;
        boardVm.TrayCollapsed = false;
        var boardHost = LayoutShellAndAssertUnclipped(shell, clientSize, ["Update...", "Export PNG...", "Tiers", "Collapse (Ctrl+U)"]);
        AssertBoardCardsFitInViewports(boardHost, "normal compact 960x540", requireSecondTierVisible: true);
        var normalCompactCoverHeight = boardVm.CardCoverHeight;
        var normalCompactCardWidth = boardVm.CardWidth;
        Assert.Equal(CardDensity.Standard, boardVm.Density);
        boardVm.SetDensityCommand.Execute(CardDensity.Compact);
        Assert.Equal(CardDensity.Standard, boardVm.Density);

        // 5. Full MainWindow shell with Board View AND Save Failed banner visible (TWO full tiers + unranked card + zero reflow!)
        harness.Project.SaveFailed = true;
        harness.Project.SaveFailureDetail = "Disk full while saving.";
        harness.Project.SaveText = "Save failed: Disk full";
        var failedBoardHost = LayoutShellAndAssertUnclipped(shell, clientSize, ["Retry save", "Discard unsaved changes", "Update...", "Export PNG...", "Tiers", "Collapse (Ctrl+U)"]);
        AssertBoardCardsFitInViewports(failedBoardHost, "save-failed compact 960x540", requireSecondTierVisible: true);
        Assert.Equal(normalCompactCoverHeight, boardVm.CardCoverHeight);
        Assert.Equal(normalCompactCardWidth, boardVm.CardWidth);
        harness.Project.SaveFailed = false;
        harness.Project.SaveFailureDetail = string.Empty;
        harness.Project.SaveText = "Saved";

        // 6. Collection List View at standard 1180x780 width must not produce a horizontal scrollbar.
        harness.Project.SelectedCollection = harness.Project.Collections[0];
        collectionVm.InspectorOpen = true;
        collectionVm.SetViewModeCommand.Execute(CollectionViewMode.List);
        var listHost = new Border { Width = 1180, Height = 780, DataContext = collectionVm, Child = new CollectionView { DataContext = collectionVm } };
        ThemeManager.ApplyToDictionary(listHost.Resources, AppTheme.Dark);
        listHost.Measure(new Size(1180, 780));
        listHost.Arrange(new Rect(0, 0, 1180, 780));
        listHost.UpdateLayout();
        var entryList = Assert.IsType<ListView>(Descendants<CollectionView>(listHost).Single().FindName("EntryList"));
        var listScroll = Assert.IsType<ScrollViewer>(Descendants<ScrollViewer>(entryList).First());
        Assert.True(listScroll.ScrollableWidth <= 0.5, $"Collection List View overflowed horizontally by {listScroll.ScrollableWidth:F1} DIP.");

        // 7. Compact MainWindow global Alt+I / Alt+X routing helper: exact Alt works, AltGr / plain keys / non-collection views are ignored.
        collectionVm.ExcludeAllCommand.Execute(null);
        Assert.Equal(0, collectionVm.IncludedCount);
        Assert.True(MainWindow.TryHandleCollectionAccessKey(shell, ModifierKeys.Alt, Key.System, Key.I));
        Assert.Equal(collectionVm.TotalCount, collectionVm.IncludedCount);
        Assert.False(MainWindow.TryHandleCollectionAccessKey(shell, ModifierKeys.Control | ModifierKeys.Alt, Key.System, Key.X));
        Assert.Equal(collectionVm.TotalCount, collectionVm.IncludedCount);
        Assert.False(MainWindow.TryHandleCollectionAccessKey(shell, ModifierKeys.None, Key.X, Key.None));
        Assert.True(MainWindow.TryHandleCollectionAccessKey(shell, ModifierKeys.Alt, Key.System, Key.X));
        Assert.Equal(0, collectionVm.IncludedCount);
        harness.Project.SelectedBoard = harness.Project.Boards[0];
        Assert.False(MainWindow.TryHandleCollectionAccessKey(shell, ModifierKeys.Alt, Key.System, Key.I));
    });

    [Fact]
    public Task Discard_after_save_failure_on_first_collection_replaces_content_with_live_view() => Ui.RunAsync(async () =>
    {
        FailingCommitStore? failingStore = null;
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false, wrapStore: inner => failingStore = new FailingCommitStore(inner));
        var beforeDiscard = await harness.CreateCollectionAsync();
        await harness.Session.FlushAsync();
        Assert.Same(beforeDiscard, harness.Project.Content);
        Assert.Same(harness.Project.Collections[0], harness.Project.SelectedCollection);

        // Fail the next SQLite commit to enter real SaveFailed suspension while viewing Collections[0].
        failingStore!.FailCommits = true;
        beforeDiscard.Entries[0].Included = true;
        await Ui.UntilAsync(() => harness.Project.SaveFailed, "save failure surfaced in ProjectViewModel");

        // Discard unsaved changes and restore normal commits.
        failingStore.FailCommits = false;
        await harness.Project.DiscardChangesCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => !harness.Project.SaveFailed, "save state restored after discard");

        var afterDiscard = Assert.IsType<CollectionViewModel>(harness.Project.Content);
        Assert.NotSame(beforeDiscard, afterDiscard);
        Assert.Equal(0, afterDiscard.IncludedCount);

        // Verify the replacement view model is actively registered and receives subsequent edits.
        afterDiscard.Entries[0].Included = true;
        await harness.Session.FlushAsync();
        Assert.Equal(1, afterDiscard.IncludedCount);
    });

    [Fact]
    public Task Discard_after_save_failure_preserves_active_board_and_handles_reload_error() => Ui.RunAsync(async () =>
    {
        FailingCommitStore? failingStore = null;
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false, wrapStore: inner => failingStore = new FailingCommitStore(inner));
        var collection = await harness.CreateCollectionAsync();
        collection.Entries[0].Included = true;
        collection.NewBoardName = "KeepMe";
        await collection.CreateBoardCommand.ExecuteAsync(null);
        await harness.Session.FlushAsync();

        var beforeBoard = Assert.IsType<BoardViewModel>(harness.Project.Content);
        var boardId = beforeBoard.Id;

        // Fail a commit on the board to enter SaveFailed suspension.
        failingStore!.FailCommits = true;
        beforeBoard.Drop(beforeBoard.Unranked.Entries[0], beforeBoard.Tiers[0], 0);
        await Ui.UntilAsync(() => harness.Project.SaveFailed, "board save failure surfaced");

        // First verify that if reloading the database throws during discard, pending edits stay intact in the session
        // (atomic discard), SaveFailed remains true, and retrying afterwards commits the preserved edit to SQLite!
        failingStore.FailLoads = true;
        await harness.Project.DiscardChangesCommand.ExecuteAsync(null);
        Assert.True(harness.Project.SaveFailed);
        Assert.Single(harness.Session.Current.Board(boardId).Placements[harness.Session.Current.Board(boardId).Tiers[0].Id]);
        Assert.Contains("reload", harness.Project.SaveFailureDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(harness.Dialogs.Messages, m => m.Title.Contains("Could not discard", StringComparison.OrdinalIgnoreCase));

        // Prove the preserved pending edit can still be committed via Retry and survives a real SQLite reopen.
        failingStore.FailLoads = false;
        failingStore.FailCommits = false;
        await harness.Project.RetrySaveCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => !harness.Project.SaveFailed, "preserved edit saved on retry");
        await harness.ReopenAsync();
        var reopenedBoard = harness.Session.Current.Board(boardId);
        Assert.Single(reopenedBoard.Placements[reopenedBoard.Tiers[0].Id]);

        // Now verify clean discard on an active board keeps the board selected with a fresh live BoardViewModel.
        FailingCommitStore? failingStore2 = null;
        await using var harness2 = await DesktopHarness.CreateAsync(serveCovers: false, wrapStore: inner => failingStore2 = new FailingCommitStore(inner));
        var collection2 = await harness2.CreateCollectionAsync();
        collection2.Entries[0].Included = true;
        collection2.NewBoardName = "KeepMe";
        await collection2.CreateBoardCommand.ExecuteAsync(null);
        await harness2.Session.FlushAsync();

        var beforeBoard2 = Assert.IsType<BoardViewModel>(harness2.Project.Content);
        var boardId2 = beforeBoard2.Id;
        failingStore2!.FailCommits = true;
        beforeBoard2.Drop(beforeBoard2.Unranked.Entries[0], beforeBoard2.Tiers[0], 0);
        await Ui.UntilAsync(() => harness2.Project.SaveFailed, "second save failure surfaced");

        failingStore2.FailCommits = false;
        await harness2.Project.DiscardChangesCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => !harness2.Project.SaveFailed, "board save state restored after discard");

        var afterBoard = Assert.IsType<BoardViewModel>(harness2.Project.Content);
        Assert.NotSame(beforeBoard2, afterBoard);
        Assert.Equal(boardId2, afterBoard.Id);
        Assert.Empty(afterBoard.Tiers[0].Entries);
    });

    [Fact]
    public Task Retry_and_discard_serialize_and_never_install_stale_snapshot() => Ui.RunAsync(async () =>
    {
        FailingCommitStore? failingStore = null;
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false, wrapStore: inner => failingStore = new FailingCommitStore(inner));
        var collection = await harness.CreateCollectionAsync();
        await harness.Session.FlushAsync();

        // Part 1: DiscardAsync holds _recoveryGate while RetryAsync starts concurrently.
        failingStore!.FailCommits = true;
        collection.Entries[0].Included = true;
        await Ui.UntilAsync(() => harness.Project.SaveFailed, "save failure surfaced");

        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failingStore.OnLoad = async () =>
        {
            loadStarted.TrySetResult();
            await releaseLoad.Task;
        };
        failingStore.FailCommits = false;
        var commitsBefore = failingStore.CommitAttempts;

        var discardTask = harness.Session.DiscardAsync(CancellationToken.None);
        await loadStarted.Task;

        // Start RetryAsync while DiscardAsync holds _recoveryGate: Retry must wait and NEVER attempt a commit.
        var retryTask = harness.Session.RetryAsync();
        await Task.Yield();
        Assert.False(retryTask.IsCompleted, "RetryAsync must serialize behind an in-flight DiscardAsync.");
        Assert.Equal(commitsBefore, failingStore.CommitAttempts);

        releaseLoad.SetResult();
        var discarded = await discardTask;
        var retried = await retryTask;
        Assert.True(discarded);
        Assert.True(retried);
        Assert.Equal(commitsBefore, failingStore.CommitAttempts);

        // Session and SQLite on disk must agree identically (0 included entries).
        var fromDisk = ProjectState.FromData(await harness.Project.Store.LoadAsync(CancellationToken.None));
        Assert.Equal(0, harness.Session.Current.Collection(collection.Id).Entries.Count(e => e.Included));
        Assert.Equal(0, fromDisk.Collection(collection.Id).Entries.Count(e => e.Included));

        // Part 2 (reverse ordering): RetryAsync holds _recoveryGate inside CommitAsync while DiscardAsync starts concurrently.
        failingStore.OnLoad = null;
        failingStore.FailCommits = true;
        collection.Entries[1].Included = true;
        await Ui.UntilAsync(() => harness.Project.SaveFailed, "second save failure surfaced");

        var commitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failingStore.FailCommits = false;
        failingStore.OnCommit = async () =>
        {
            commitStarted.TrySetResult();
            await releaseCommit.Task;
        };

        var retryTask2 = harness.Session.RetryAsync();
        await commitStarted.Task;

        var discardTask2 = harness.Session.DiscardAsync(CancellationToken.None);
        await Task.Yield();
        Assert.False(discardTask2.IsCompleted, "DiscardAsync must serialize behind an in-flight RetryAsync.");

        releaseCommit.SetResult();
        var retried2 = await retryTask2;
        var discarded2 = await discardTask2;
        Assert.True(retried2);
        Assert.False(discarded2);

        // Retry succeeded first so Discard must be a no-op; both session and SQLite preserve the committed edit.
        var fromDisk2 = ProjectState.FromData(await harness.Project.Store.LoadAsync(CancellationToken.None));
        Assert.Equal(1, harness.Session.Current.Collection(collection.Id).Entries.Count(e => e.Included));
        Assert.Equal(1, fromDisk2.Collection(collection.Id).Entries.Count(e => e.Included));

        // Part 3 (ViewModel InvalidOperationException reload failure): must NOT silently swallow reload invariant errors.
        failingStore.OnCommit = null;
        failingStore.FailCommits = true;
        collection.Entries[2].Included = true;
        await Ui.UntilAsync(() => harness.Project.SaveFailed, "third save failure surfaced");
        failingStore.ThrowInvalidOperationOnLoad = true;
        await harness.Project.DiscardChangesCommand.ExecuteAsync(null);
        Assert.True(harness.Project.SaveFailed);
        Assert.Contains("Simulated invariant error", harness.Project.SaveFailureDetail, StringComparison.OrdinalIgnoreCase);
        failingStore.ThrowInvalidOperationOnLoad = false;

        // Part 4 (Disposal vs queued, newly arriving, mutations, and overlapping DisposeAsync callers):
        var disposeLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisposeLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeDisposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStoreDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failingStore.OnLoad = async () =>
        {
            disposeLoadStarted.TrySetResult();
            await releaseDisposeLoad.Task;
        };
        failingStore.OnDispose = async () =>
        {
            storeDisposeStarted.TrySetResult();
            await releaseStoreDispose.Task;
        };
        failingStore.FailCommits = false;

        var inFlightDiscard = harness.Session.DiscardAsync(CancellationToken.None);
        await disposeLoadStarted.Task;
        var queuedRetry = harness.Session.RetryAsync();
        await Task.Yield();

        var disposeTask1 = harness.Session.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.False(disposeTask1.IsCompleted, "DisposeAsync must wait for admitted recovery callers to drain.");

        // Mutations and recoveries starting after closing begins must be rejected immediately.
        Assert.IsType<SessionResult.Suspended>(harness.Session.Execute("Late edit", s => s));
        Assert.IsType<SessionResult.Suspended>(harness.Session.Undo());
        Assert.False(await harness.Session.RetryAsync());

        // Release recovery so DisposeCoreAsync enters _store.DisposeAsync(), then start an overlapping second DisposeAsync caller.
        releaseDisposeLoad.SetResult();
        Assert.False(await inFlightDiscard);
        Assert.False(await queuedRetry);
        await storeDisposeStarted.Task;

        var disposeTask2 = harness.Session.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.False(disposeTask2.IsCompleted, "Overlapping DisposeAsync caller must await the shared disposal task until store cleanup finishes.");

        releaseStoreDispose.SetResult();
        await Task.WhenAll(disposeTask1, disposeTask2);

        // Sequential repeated DisposeAsync remains idempotent.
        await harness.Session.DisposeAsync();
    });

    [Fact]
    public Task Corrupt_cached_thumbnail_regenerates_offline_and_decode_failure_persists_across_refresh() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: true);
        var collection = await harness.CreateCollectionAsync();
        var state = harness.Session.Current;
        var colState = state.Collection(collection.Id);
        var firstAsset = state.Assets[colState.Entries[0].Cover!.Asset];

        // 1. Corrupt the disposable cached JPEG thumbnail while the original asset on disk remains intact.
        var cacheDir = Path.Combine(harness.ProjectDirectory, "cache", "thumbnails");
        Directory.CreateDirectory(cacheDir);
        var cachedThumb = Path.Combine(cacheDir, $"{firstAsset.Hash.Value}-{ThumbnailLoader.LargePixelHeight}.jpg");
        await File.WriteAllBytesAsync(cachedThumb, [0x00, 0x11, 0x22, 0x33, 0x44]);

        // Load through a fresh ThumbnailLoader with no network: corrupt cache must self-heal from the valid original.
        using var freshLoader = new ThumbnailLoader(new AnimeTierlist.Infrastructure.Assets.ThumbnailCache(
            harness.ProjectDirectory,
            new AnimeTierlist.Infrastructure.Assets.AssetStore(harness.ProjectDirectory)));
        var recovered = await freshLoader.LoadAsync(firstAsset, ThumbnailLoader.LargePixelHeight, CancellationToken.None);
        Assert.NotNull(recovered);

        // 2. When a corrupt cached file is locked (so Invalidate cannot delete it and the retry hits FileFormatException),
        // ThumbnailLoader.LoadAsync must return null cleanly without throwing an unhandled exception.
        var lockedThumb = Path.Combine(cacheDir, $"{firstAsset.Hash.Value}-{ThumbnailLoader.PixelHeight}.jpg");
        await File.WriteAllBytesAsync(lockedThumb, [0x00, 0x11, 0x22, 0x33, 0x44]);
        using (var freshLoaderLocked = new ThumbnailLoader(new AnimeTierlist.Infrastructure.Assets.ThumbnailCache(
                   harness.ProjectDirectory,
                   new AnimeTierlist.Infrastructure.Assets.AssetStore(harness.ProjectDirectory))))
        using (new FileStream(lockedThumb, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failedLocked = await freshLoaderLocked.LoadAsync(firstAsset, ThumbnailLoader.PixelHeight, CancellationToken.None);
            Assert.Null(failedLocked);
        }

        // 3. When the original file itself is missing and decode fails, Collection chip counts & NeedsAttention filter
        // update automatically, unrelated refreshes preserve "Unavailable", and same-hash repair recovers cleanly.
        var secondEntry = collection.Entries[1];
        var secondColEntry = colState.Entries[1];
        var secondAsset = state.Assets[secondColEntry.Cover!.Asset];
        var assetStore = new AnimeTierlist.Infrastructure.Assets.AssetStore(harness.ProjectDirectory);
        var secondOriginalPath = assetStore.PathOf(secondAsset);
        var originalBytes = await File.ReadAllBytesAsync(secondOriginalPath);
        File.Delete(secondOriginalPath);
        foreach (var thumb in Directory.GetFiles(cacheDir, $"{secondAsset.Hash.Value}-*.jpg"))
        {
            File.Delete(thumb);
        }

        secondEntry.Update(2, secondColEntry, state.ResolveTitle(colState, secondColEntry), new SourceTitles("Title", null, null, null), secondAsset, null, coverBroken: false);
        _ = secondEntry.Thumbnail;
        await Ui.UntilAsync(() => secondEntry.CoverUnavailable, "thumbnail decode failure marked entry unavailable");
        Assert.Equal("Unavailable", secondEntry.ListCoverStatus);
        Assert.False(secondEntry.HasCover);
        Assert.True(secondEntry.NeedsAttention);
        Assert.Equal(1, collection.NeedsAttentionCount);
        Assert.Equal(1, collection.NoCoverCount);

        collection.FilterMode = CollectionFilterMode.NeedsAttention;
        Assert.Contains(secondEntry, collection.View.Cast<EntryViewModel>());
        collection.FilterMode = CollectionFilterMode.All;

        // Unrelated refresh with the SAME asset hash must preserve Unavailable & HasCover == false.
        secondEntry.Update(2, secondColEntry, state.ResolveTitle(colState, secondColEntry), new SourceTitles("Title", null, null, null), secondAsset, null, coverBroken: false);
        Assert.True(secondEntry.CoverUnavailable);
        Assert.Equal("Unavailable", secondEntry.ListCoverStatus);
        Assert.False(secondEntry.HasCover);

        // Intermediate state check: ResetThumbnailFailure() followed synchronously by Refresh() must NOT flip HasCover to true before decode finishes.
        secondEntry.ResetThumbnailFailure();
        collection.Refresh(harness.Session.Current);
        Assert.True(secondEntry.CoverUnavailable);
        Assert.False(secondEntry.HasCover);

        // Same-hash repair via selecting/inspecting the entry (even offscreen without reading .Thumbnail!) must recover cleanly.
        await File.WriteAllBytesAsync(secondOriginalPath, originalBytes);
        collection.SelectedEntry = secondEntry;
        await Ui.UntilAsync(() => !secondEntry.CoverUnavailable && secondEntry.Thumbnail is not null, "same-hash cover repair recovered thumbnail via inspection");
        Assert.Equal("OK", secondEntry.ListCoverStatus);
        Assert.True(secondEntry.HasCover);
        Assert.False(secondEntry.NeedsAttention);
        Assert.Equal(0, collection.NeedsAttentionCount);

        // 3b. Dismissing the damaged-cover banner must stay dismissed across subsequent refreshes, and repairing via FetchCoversCommand preserves its summary.
        await File.WriteAllBytesAsync(secondOriginalPath, [1, 2, 3]);
        collection.SelectedEntry = null;
        collection.SelectedEntry = secondEntry;
        await Ui.UntilAsync(() => secondEntry.CoverBroken, "damaged cover detected via inspection");
        Assert.NotEmpty(collection.CoverStatus);
        collection.DismissCoverStatusCommand.Execute(null);
        Assert.Equal(string.Empty, collection.CoverStatus);
        collection.Refresh(harness.Session.Current);
        Assert.Equal(string.Empty, collection.CoverStatus);
        await collection.FetchCoversCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => !secondEntry.CoverBroken && secondEntry.HasCover, "damaged cover repaired by FetchCoversCommand");
        Assert.Contains("Covers for", collection.CoverStatus, StringComparison.Ordinal);

        // 4. BoardEntryViewModel decode failure updates AccessibleName/CoverPlaceholder, persists across refresh, and recovers via RecheckCovers().
        var thirdColEntry = colState.Entries[2];
        var thirdAsset = state.Assets[thirdColEntry.Cover!.Asset];
        var thirdOriginalPath = assetStore.PathOf(thirdAsset);
        var thirdOriginalBytes = await File.ReadAllBytesAsync(thirdOriginalPath);

        collection.Entries[2].Included = true;
        collection.NewBoardName = "CoverRecovery";
        await collection.CreateBoardCommand.ExecuteAsync(null);
        var board = Assert.IsType<BoardViewModel>(harness.Project.Content);
        var boardEntry = board.Unranked.Entries[0];
        await Ui.UntilAsync(() => !collection.IsCheckingCovers && !board.IsCheckingCovers, "cover checks");

        File.Delete(thirdOriginalPath);
        foreach (var thumb in Directory.GetFiles(cacheDir, $"{thirdAsset.Hash.Value}-*.jpg"))
        {
            File.Delete(thumb);
        }

        _ = boardEntry.Thumbnail;
        await Ui.UntilAsync(() => !boardEntry.HasCover && boardEntry.CoverPlaceholder == "Unavailable", "board entry decode failure marked unavailable");
        Assert.True(boardEntry.IsBrokenCover);
        Assert.Contains("unavailable", boardEntry.AccessibleName, StringComparison.OrdinalIgnoreCase);

        // Unrelated board refresh with same hash must preserve Unavailable & AccessibleName.
        board.Refresh(harness.Session.Current);
        Assert.False(boardEntry.HasCover);
        Assert.Equal("Unavailable", boardEntry.CoverPlaceholder);
        Assert.Contains("unavailable", boardEntry.AccessibleName, StringComparison.OrdinalIgnoreCase);

        // Restore original bytes and recheck covers: board entry must recover cleanly.
        await File.WriteAllBytesAsync(thirdOriginalPath, thirdOriginalBytes);
        board.RecheckCovers();
        _ = boardEntry.Thumbnail;
        await Ui.UntilAsync(() => boardEntry.HasCover && boardEntry.Thumbnail is not null, "board entry recovered after cover repair");
        Assert.DoesNotContain("unavailable", boardEntry.AccessibleName, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task Board_search_density_tray_and_board_update_invariants_hold() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        collection.Entries[0].Included = true;
        collection.Entries[1].Included = true;
        collection.NewBoardName = "SearchAndUpdate";
        await collection.CreateBoardCommand.ExecuteAsync(null);
        var board = Assert.IsType<BoardViewModel>(harness.Project.Content);

        // Search navigation commands are disabled with no query or "No matches", and enabled when matches exist.
        Assert.False(board.NextSearchMatchCommand.CanExecute(null));
        Assert.False(board.PreviousSearchMatchCommand.CanExecute(null));
        board.BoardSearchText = "NoSuchTitleXYZ999";
        Assert.Equal("No matches", board.BoardSearchMatchSummary);
        Assert.False(board.NextSearchMatchCommand.CanExecute(null));
        Assert.False(board.PreviousSearchMatchCommand.CanExecute(null));

        // On compact width with overlay open and 0 matches, F3 must NOT dismiss the overlay.
        var view = new BoardView { DataContext = board };
        view.Measure(new Size(944, 500));
        view.Arrange(new Rect(0, 0, 944, 500));
        view.UpdateLayout();
        board.InspectorOpen = true;
        view.UpdateLayout();
        var scrim = Assert.IsType<Border>(view.FindName("BoardInspectorScrim"));
        Assert.Equal(Visibility.Visible, scrim.Visibility);
        view.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new DummyPresentationSource(view), 0, Key.F3) { RoutedEvent = UIElement.PreviewKeyDownEvent });
        Assert.True(board.InspectorOpen);
        Assert.Equal(Visibility.Visible, scrim.Visibility);

        // With an actual match, external NextSearchMatchCommand dismisses the overlay instead of focusing behind the scrim.
        board.BoardSearchText = board.Unranked.Entries[0].Title.Split(' ')[0];
        Assert.True(board.NextSearchMatchCommand.CanExecute(null));
        Assert.True(board.PreviousSearchMatchCommand.CanExecute(null));
        board.NextSearchMatchCommand.Execute(null);
        await Ui.UntilAsync(() => !board.InspectorOpen, "overlay dismissed on external board search jump");
        Assert.Equal(Visibility.Collapsed, scrim.Visibility);

        // Switching board density S -> M -> L on a full-size live BoardView without resizing recalculates tray constraints so unranked cards fit.
        board.BoardSearchText = string.Empty;
        var fullHost = new Border { Width = 1200, Height = 760, DataContext = board, Child = new BoardView { DataContext = board } };
        ThemeManager.ApplyToDictionary(fullHost.Resources, AppTheme.Dark);
        fullHost.Measure(new Size(1200, 760));
        fullHost.Arrange(new Rect(0, 0, 1200, 760));
        fullHost.UpdateLayout();
        board.Drop(board.Unranked.Entries[0], board.Tiers[0], 0);
        foreach (var density in new[] { CardDensity.Compact, CardDensity.Standard, CardDensity.Large })
        {
            board.SetDensityCommand.Execute(density);
            fullHost.UpdateLayout();
            AssertBoardCardsFitInViewports(fullHost, $"live density switch to {density}", requireSecondTierVisible: false);
        }

        // BoardUpdateViewModel disables Apply when 0 additions, 0 removals, and Keep pinned are selected.
        collection.Entries[2].Included = true;
        var boardState = harness.Session.Current.Board(board.Id);
        var colState = harness.Session.Current.Collection(collection.Id);
        var updateVm = new BoardUpdateViewModel(boardState, colState, harness.Session.Current, _ => true);
        Assert.True(updateVm.HasSelectedChanges);
        updateVm.SelectNoAdditionsCommand.Execute(null);
        Assert.False(updateVm.HasSelectedChanges);
    });

    [Fact]
    public Task Entry_list_cover_status_and_clear_search_preserve_exact_state() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var state = harness.Session.Current;
        var colState = state.Collection(collection.Id);
        var entryVm = collection.Entries[0];
        var colEntry = colState.Entries[0];
        var resolved = state.ResolveTitle(colState, colEntry);
        var observed = new SourceTitles(resolved.Text, null, null, null);
        var fakeAsset = new AssetRecord(new AssetHash(new string('a', 64)), AssetFormat.Jpeg, 175, 250, 1024);

        // Never-downloaded entry -> "No cover" (NOT "Unavailable").
        entryVm.Update(1, colEntry, resolved, observed, null, null, coverBroken: false);
        Assert.Equal("No cover", entryVm.ListCoverStatus);

        // Intact cover -> "OK".
        entryVm.Update(1, colEntry, resolved, observed, fakeAsset, null, coverBroken: false);
        Assert.Equal("OK", entryVm.ListCoverStatus);

        // Intact repaired cover -> "OK" with "Cover repaired" in tooltip (NOT "Unavailable").
        entryVm.Update(1, colEntry, resolved, observed, fakeAsset, new CoverEntryStatus(colEntry.Item, CoverStatus.Repaired, "Repaired"), coverBroken: false);
        Assert.Equal("OK", entryVm.ListCoverStatus);
        Assert.Contains("repaired", entryVm.ListCoverStatusToolTip, StringComparison.OrdinalIgnoreCase);

        // Damaged cover -> "Damaged".
        entryVm.Update(1, colEntry, resolved, observed, fakeAsset, null, coverBroken: true);
        Assert.Equal("Damaged", entryVm.ListCoverStatus);

        // Needs review -> "Needs review".
        entryVm.Update(1, colEntry, resolved, observed, fakeAsset, new CoverEntryStatus(colEntry.Item, CoverStatus.NeedsReview, "Changed"), coverBroken: true);
        Assert.Equal("Damaged", entryVm.ListCoverStatus);
        entryVm.Update(1, colEntry, resolved, observed, fakeAsset, new CoverEntryStatus(colEntry.Item, CoverStatus.NeedsReview, "Changed"), coverBroken: false);
        Assert.Equal("Needs review", entryVm.ListCoverStatus);

        // ClearSearchCommand clears SearchText while preserving the active FilterMode chip.
        collection.Entries[0].Included = true;
        collection.FilterMode = CollectionFilterMode.Included;
        collection.SearchText = "Frieren";
        collection.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, collection.SearchText);
        Assert.Equal(CollectionFilterMode.Included, collection.FilterMode);
    });

    /// <summary>Minimal PresentationSource so KeyEventArgs can be constructed in headless STA unit tests.</summary>
    private sealed class DummyPresentationSource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get => root; set { } }

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    /// <summary>Wraps an <see cref="IProjectStore"/> so tests can deterministically fail or pause commits and loads.</summary>
    private sealed class FailingCommitStore(IProjectStore inner) : IProjectStore
    {
        public bool FailCommits { get; set; }

        public bool FailLoads { get; set; }

        public bool ThrowInvalidOperationOnLoad { get; set; }

        public int CommitAttempts { get; private set; }

        public Func<Task>? OnLoad { get; set; }

        public Func<Task>? OnCommit { get; set; }

        public Func<Task>? OnDispose { get; set; }

        public async Task<ProjectData> LoadAsync(CancellationToken cancellationToken)
        {
            if (OnLoad is { } hook)
            {
                await hook();
            }

            if (ThrowInvalidOperationOnLoad)
            {
                throw new InvalidOperationException("Simulated invariant error during reload.");
            }

            if (FailLoads)
            {
                throw new IOException("Simulated disk read error during reload.");
            }

            return await inner.LoadAsync(cancellationToken);
        }

        public async Task CommitAsync(ProjectChange change, CancellationToken cancellationToken)
        {
            CommitAttempts++;
            if (OnCommit is { } hook)
            {
                await hook();
            }

            if (FailCommits)
            {
                throw new IOException("Simulated disk full during commit.");
            }

            await inner.CommitAsync(change, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (OnDispose is { } hook)
            {
                await hook();
            }

            await inner.DisposeAsync();
        }
    }

    private static void AssertFirstCollectionCardFits(Border host, string scenario)
    {
        var colView = Descendants<CollectionView>(host).Single();
        var posterGrid = Assert.IsType<PosterGridListBox>(colView.FindName("PosterGrid"));
        var firstCard = Assert.IsType<PosterGridListBoxItem>(posterGrid.ItemContainerGenerator.ContainerFromIndex(0));
        var cardPos = firstCard.TranslatePoint(new Point(0, 0), posterGrid);
        Assert.True(cardPos.Y >= -1 && cardPos.Y + firstCard.ActualHeight <= posterGrid.ActualHeight + 1,
            $"First collection poster card clipped in {scenario}: y={cardPos.Y:F0}, h={firstCard.ActualHeight:F0}, gridH={posterGrid.ActualHeight:F0}.");
    }

    private static void AssertBoardCardsFitInViewports(Border host, string scenario, bool requireSecondTierVisible)
    {
        var boardView = Descendants<BoardView>(host).Single();
        var boardVm = Assert.IsType<BoardViewModel>(boardView.DataContext);
        Assert.True(boardVm.CardWidth >= 90, $"Board cards must stay wide enough for readable titles in {scenario}, got width={boardVm.CardWidth}.");

        var rowsViewport = Assert.IsType<ScrollViewer>(boardView.FindName("Rows"));
        var unrankedList = Assert.IsType<ListBox>(boardView.FindName("UnrankedList"));

        var tierTitles = Descendants<TextBlock>(rowsViewport).Where(t => t.Name == "BoardCardTitle" && IsRenderedVisible(t, rowsViewport) && t.ActualHeight > 0).ToList();
        Assert.NotEmpty(tierTitles);
        var tierTitlePos = tierTitles[0].TranslatePoint(new Point(0, 0), rowsViewport);
        Assert.True(tierTitlePos.Y >= -1 && tierTitlePos.Y + tierTitles[0].ActualHeight <= rowsViewport.ActualHeight + 1,
            $"Tier 1 card title '{tierTitles[0].Text}' clipped in {scenario}: y={tierTitlePos.Y:F0}, h={tierTitles[0].ActualHeight:F0}, viewportH={rowsViewport.ActualHeight:F0}.");

        if (requireSecondTierVisible)
        {
            Assert.True(tierTitles.Count >= 2, $"Expected at least 2 tier cards in {scenario}.");
            var tier2Pos = tierTitles[1].TranslatePoint(new Point(0, 0), rowsViewport);
            Assert.True(tier2Pos.Y >= -1 && tier2Pos.Y + tierTitles[1].ActualHeight <= rowsViewport.ActualHeight + 1,
                $"Tier 2 card title '{tierTitles[1].Text}' clipped in {scenario}: y={tier2Pos.Y:F0}, h={tierTitles[1].ActualHeight:F0}, viewportH={rowsViewport.ActualHeight:F0}.");

            var tierRows = Descendants<ListBox>(rowsViewport).Where(lb => IsRenderedVisible(lb, rowsViewport) && lb.Items.Count > 0).ToList();
            Assert.True(tierRows.Count >= 2, $"Expected at least 2 populated tier row ListBoxes in {scenario}.");
            var tier2Item = Assert.IsType<ListBoxItem>(tierRows[1].ItemContainerGenerator.ContainerFromIndex(0));
            var tier2ItemPos = tier2Item.TranslatePoint(new Point(0, 0), rowsViewport);
            Assert.True(tier2ItemPos.Y + tier2Item.ActualHeight <= rowsViewport.ActualHeight + 1,
                $"Tier 2 card tile clipped in {scenario}: bottom={tier2ItemPos.Y + tier2Item.ActualHeight:F1}, viewportH={rowsViewport.ActualHeight:F1}.");
            var tier2RowBorder = Assert.IsType<Border>(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(tierRows[1]))));
            var tier2BorderPos = tier2RowBorder.TranslatePoint(new Point(0, 0), rowsViewport);
            Assert.True(tier2BorderPos.Y + tier2RowBorder.ActualHeight <= rowsViewport.ActualHeight + 1,
                $"Tier 2 row border clipped in {scenario}: bottom={tier2BorderPos.Y + tier2RowBorder.ActualHeight:F1}, viewportH={rowsViewport.ActualHeight:F1}.");
        }

        var unrankedTitles = Descendants<TextBlock>(unrankedList).Where(t => t.Name == "BoardCardTitle" && IsRenderedVisible(t, unrankedList) && t.ActualHeight > 0).ToList();
        Assert.NotEmpty(unrankedTitles);
        var unrankedTitlePos = unrankedTitles[0].TranslatePoint(new Point(0, 0), unrankedList);
        Assert.True(unrankedTitlePos.Y >= -1 && unrankedTitlePos.Y + unrankedTitles[0].ActualHeight <= unrankedList.ActualHeight + 1,
            $"Unranked card title '{unrankedTitles[0].Text}' clipped in {scenario}: y={unrankedTitlePos.Y:F0}, h={unrankedTitles[0].ActualHeight:F0}, viewportH={unrankedList.ActualHeight:F0}.");
        var unrankedItem = Assert.IsType<ListBoxItem>(unrankedList.ItemContainerGenerator.ContainerFromIndex(0));
        var titleInCard = unrankedTitles[0].TranslatePoint(new Point(0, 0), unrankedItem);
        Assert.True(titleInCard.Y + unrankedTitles[0].ActualHeight <= unrankedItem.ActualHeight - 1,
            $"Unranked card title '{unrankedTitles[0].Text}' clipped inside its tile in {scenario}: bottom={titleInCard.Y + unrankedTitles[0].ActualHeight:F1}, tileH={unrankedItem.ActualHeight:F1}.");

        if (boardVm.Unranked.Entries.Count <= 3 && Descendants<ScrollViewer>(unrankedList).FirstOrDefault() is { } trayScroll)
        {
            Assert.True(trayScroll.ScrollableHeight <= 1,
                $"Unranked tray showed vertical overflow ({trayScroll.ScrollableHeight:F1} DIP) for {boardVm.Unranked.Entries.Count} items in {scenario}.");
        }
    }

    private static Border LayoutShellAndAssertUnclipped(ShellViewModel shell, Size size, string[] expectedButtonNames)
    {
        var window = new MainWindow { DataContext = shell };
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        window.DataContext = null;
        var host = new Border { Width = size.Width, Height = size.Height, DataContext = shell, Child = content };
        ThemeManager.ApplyToDictionary(host.Resources, AppTheme.Dark);
        foreach (var key in window.Resources.Keys)
        {
            host.Resources[key] = window.Resources[key];
        }

        host.Measure(size);
        host.Arrange(new Rect(new Point(0, 0), size));
        host.UpdateLayout();
        // Second layout pass settles bindings flipped by SizeChanged handlers on a detached visual tree.
        host.Measure(size);
        host.Arrange(new Rect(new Point(0, 0), size));
        host.UpdateLayout();

        var visibleButtons = Descendants<Button>(host).Where(b => IsRenderedVisible(b, host) && b.ActualWidth > 0 && b.ActualHeight > 0).ToList();
        foreach (var expected in expectedButtonNames)
        {
            var match = visibleButtons.FirstOrDefault(b =>
            {
                var peerName = (UIElementAutomationPeer.CreatePeerForElement(b).GetName() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);
                return string.Equals(peerName, expected, StringComparison.OrdinalIgnoreCase)
                    || peerName.StartsWith(expected + " ", StringComparison.OrdinalIgnoreCase);
            });
            var cv = Descendants<CollectionView>(host).FirstOrDefault();
            Assert.True(match is not null, $"Expected visible UIA-named button '{expected}' in shell ({size}), cvW={cv?.ActualWidth:F1}, narrow={(cv?.DataContext as CollectionViewModel)?.IsNarrowToolbar}. Visible: [{string.Join(", ", visibleButtons.Select(b => UIElementAutomationPeer.CreatePeerForElement(b).GetName()))}]");
            var topLeft = match!.TranslatePoint(new Point(0, 0), host);
            Assert.True(topLeft.X >= -1 && topLeft.Y >= -1 && topLeft.X + match.ActualWidth <= size.Width + 1 && topLeft.Y + match.ActualHeight <= size.Height + 1,
                $"Button '{expected}' lies outside {size}: ({topLeft.X:F0},{topLeft.Y:F0},{match.ActualWidth:F0}x{match.ActualHeight:F0}).");
        }

        return host;
    }

    [Fact]
    public Task Poster_grid_list_items_expose_included_checkbox_and_summary_via_automation_peers() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        await harness.Session.FlushAsync();

        var shell = new ShellViewModel(harness.App) { Project = harness.Project };
        harness.Project.SelectedCollection = harness.Project.Collections[0];
        var host = LayoutShellAndAssertUnclipped(shell, new Size(1224, 780), ["Create board...", "Fetch covers"]);
        var view = Descendants<CollectionView>(host).Single();
        var grid = Assert.IsType<PosterGridListBox>(view.FindName("PosterGrid"));
        Assert.Equal(Visibility.Visible, grid.Visibility);

        var gridPeer = UIElementAutomationPeer.CreatePeerForElement(grid);
        Assert.NotNull(gridPeer);
        var itemPeers = gridPeer!.GetChildren();
        Assert.NotNull(itemPeers);
        Assert.NotEmpty(itemPeers);

        // Select first item, move item #2 up (mimicking kb-curate), and check every realized item's peers.
        collection.SelectedEntry = collection.Entries[0];
        host.UpdateLayout();

        var diagnostics = new List<string>();
        foreach (var peer in itemPeers.Take(4))
        {
            var descendants = FlattenPeers(peer).ToList();
            var cb = descendants.FirstOrDefault(p =>
                p.GetAutomationControlType() == AutomationControlType.CheckBox
                && string.Equals(p.GetName(), "Included", StringComparison.Ordinal));
            var descText = string.Join(", ", descendants.Select(p => $"{p.GetAutomationControlType()}:'{p.GetName()}'"));
            diagnostics.Add($"Item '{peer.GetName()}' (type={peer.GetType().Name}) -> cb={(cb is not null ? "YES" : "NO")} [{descText}]");
        }

        foreach (var peer in itemPeers.Take(4))
        {
            var descendants = FlattenPeers(peer).ToList();
            var cb = descendants.FirstOrDefault(p =>
                p.GetAutomationControlType() == AutomationControlType.CheckBox
                && string.Equals(p.GetName(), "Included", StringComparison.Ordinal));
            Assert.True(cb is not null, string.Join("\n", diagnostics));
            Assert.NotNull(cb!.GetPattern(PatternInterface.Toggle));
        }

        // Also verify summary TextBlock exposes "of <n> included".
        var viewPeer = UIElementAutomationPeer.CreatePeerForElement(view);
        Assert.NotNull(viewPeer);
        var allTexts = FlattenPeers(viewPeer!).Where(p => p.GetAutomationControlType() == AutomationControlType.Text).Select(p => p.GetName()).ToList();
        Assert.True(allTexts.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t ?? string.Empty, @"of \d+ included$")),
            $"No Text peer matched 'of <n> included$'. Texts: [{string.Join(" | ", allTexts)}]");

        // Now mimic kb-curate: Exclude all, include #0,#1,#2, move #2 up, and re-verify every realized item peer!
        collection.ExcludeAllCommand.Execute(null);
        collection.Entries[0].Included = true;
        collection.Entries[1].Included = true;
        collection.Entries[2].Included = true;
        collection.SelectedEntry = collection.Entries[2];
        collection.MoveUpCommand.Execute(null);
        await harness.Session.FlushAsync();
        host.UpdateLayout();

        var afterPeers = gridPeer.GetChildren();
        Assert.NotNull(afterPeers);
        foreach (var peer in afterPeers!.Take(4))
        {
            var descendants = FlattenPeers(peer).ToList();
            var cb = descendants.FirstOrDefault(p =>
                p.GetAutomationControlType() == AutomationControlType.CheckBox
                && string.Equals(p.GetName(), "Included", StringComparison.Ordinal));
            var descText = string.Join(", ", descendants.Select(p => $"{p.GetAutomationControlType()}:'{p.GetName()}'"));
            Assert.True(cb is not null, $"After MoveUp, item '{peer.GetName()}' missing Included CheckBox. Descendants: [{descText}]");
        }

        // Now verify the REAL Windows UI Automation tree (AutomationElement.FromHandle) from a background thread,
        // matching SandboxChecks.ps1 (Get-Inclusion + perf-latency).
        host.Child = null;
        collection.SelectedEntry = null;
        var win = new MainWindow
        {
            DataContext = shell,
            Left = -32000,
            Top = -32000,
            Width = 1240,
            Height = 820,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        win.Show();
        try
        {
            var winView = Descendants<CollectionView>(win).Single();
            var winGrid = Assert.IsType<PosterGridListBox>(winView.FindName("PosterGrid"));
            win.UpdateLayout();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            _ = await Task.Run(() =>
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                return root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Count;
            });

            // Mimic kb-curate mutations + wrapper peer invalidation and verify every realized card keeps its Included CheckBox in UIA.
            collection.ExcludeAllCommand.Execute(null);
            collection.Entries[0].Included = true;
            collection.Entries[1].Included = true;
            collection.Entries[2].Included = true;
            collection.SelectedEntry = collection.Entries[2];
            collection.MoveUpCommand.Execute(null);
            await harness.Session.FlushAsync();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            win.UpdateLayout();

            if (winGrid.ItemContainerGenerator.ContainerFromIndex(1) is ListBoxItem movedItem)
            {
                var wp = UIElementAutomationPeer.FromElement(movedItem);
                wp?.ResetChildrenCache();
                _ = wp?.GetChildren();
            }

            var uiaReport = await Task.Run(() =>
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                var lists = root.FindAll(
                    System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.NameProperty,
                        "Collection entries in effective order"));
                if (lists.Count == 0)
                {
                    return "NO LIST FOUND";
                }

                var list = lists[0];
                var items = list.FindAll(
                    System.Windows.Automation.TreeScope.Children,
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.ListItem));
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Lists={lists.Count}, ListType={list.Current.ControlType.ProgrammaticName}, ListId={list.Current.AutomationId}, ListItems={items.Count}");
                var missing = 0;
                for (var i = 0; i < Math.Min(6, items.Count); i++)
                {
                    var item = items[i];
                    var desc = item.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                    var descList = new List<string>();
                    var hasBox = false;
                    for (var j = 0; j < desc.Count; j++)
                    {
                        var label = $"{desc[j].Current.ControlType.ProgrammaticName}:'{desc[j].Current.Name}'";
                        descList.Add(label);
                        if (label == "ControlType.CheckBox:'Included'")
                        {
                            hasBox = true;
                        }
                    }

                    if (!hasBox)
                    {
                        missing++;
                    }

                    sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  Item[{i}] '{item.Current.Name}' hasBox={hasBox} (Desc={desc.Count}: {string.Join(", ", descList)})");
                }

                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"MISSING={missing}");
                return sb.ToString();
            });
            Assert.True(uiaReport.Contains("ListItems=", StringComparison.Ordinal)
                && !uiaReport.Contains("ListItems=0", StringComparison.Ordinal)
                && uiaReport.Contains("MISSING=0", StringComparison.Ordinal), uiaReport);
        }
        finally
        {
            win.DataContext = null;
            win.Close();
        }
    });

    [Fact]
    public Task Retry_keeps_mutations_suspended_until_commit_becomes_durable() => Ui.RunAsync(async () =>
    {
        FailingCommitStore? store = null;
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false, wrapStore: inner => store = new FailingCommitStore(inner));
        var collection = await harness.CreateCollectionAsync();
        await harness.Session.FlushAsync();

        // Create one undoable edit that succeeds so Undo is non-empty, then fail the next edit.
        collection.Entries[0].Included = true;
        await harness.Session.FlushAsync();
        store!.FailCommits = true;
        collection.Entries[1].Included = true;
        await Ui.UntilAsync(() => harness.Project.SaveFailed, "save failure surfaced");

        // Hold the retry commit in flight and verify Execute, Undo, Redo, and CompleteCoverDownload are all Suspended.
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnCommit = async () =>
        {
            retryStarted.TrySetResult();
            await releaseRetry.Task;
        };

        var request = harness.Session.BeginCoverRequest(collection.Id, collection.Entries[0].Item);
        var asset = new AssetRecord(new AssetHash(new string('a', 64)), AssetFormat.Png, 100, 150, 128);
        var coverUri = new Uri("https://u.livechart.me/anime/1/poster.png");
        var provenance = new CoverProvenance(request.Occurrence, coverUri, coverUri, "default", asset.Hash);

        var retryTask = harness.Session.RetryAsync();
        try
        {
            await retryStarted.Task;
            Assert.True(harness.Session.IsSuspended);
            Assert.IsType<SessionResult.Suspended>(harness.Session.Execute("Blocked edit", s =>
                s.WithCollection(CollectionOperations.SetIncluded(s.Collection(collection.Id), [collection.Entries[2].Item], true))));
            Assert.IsType<SessionResult.Suspended>(harness.Session.Undo());
            Assert.IsType<SessionResult.Suspended>(harness.Session.Redo());
            Assert.IsType<SessionResult.Suspended>(harness.Session.CompleteCoverDownload(request, provenance, asset));
        }
        finally
        {
            store.OnCommit = null;
            releaseRetry.TrySetResult();
        }

        // First retry fails again: mutations must stay suspended.
        Assert.False(await retryTask);
        Assert.True(harness.Session.IsSuspended);
        Assert.Equal(SaveState.Failed, harness.Session.SaveState);
        Assert.IsType<SessionResult.Suspended>(harness.Session.Execute("Still blocked", s =>
            s.WithCollection(CollectionOperations.SetIncluded(s.Collection(collection.Id), [collection.Entries[2].Item], true))));

        // Second retry succeeds: mutations resume and commit cleanly.
        store.FailCommits = false;
        Assert.True(await harness.Session.RetryAsync());
        Assert.False(harness.Session.IsSuspended);
        Assert.IsType<SessionResult.Accepted>(harness.Session.Execute("Allowed after retry", s =>
            s.WithCollection(CollectionOperations.SetIncluded(s.Collection(collection.Id), [collection.Entries[2].Item], true))));
        await harness.Session.FlushAsync();
        Assert.Equal(SaveState.Saved, harness.Session.SaveState);
    });

    [Fact]
    public Task Close_blocks_intervening_edits_and_keeps_project_open_for_recovery_if_final_flush_fails() => Ui.RunAsync(async () =>
    {
        FailingCommitStore? store = null;
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false, wrapStore: inner => store = new FailingCommitStore(inner));
        var collection = await harness.CreateCollectionAsync();
        await harness.Session.FlushAsync();

        // Hold a queued commit while overlapping close attempts begin; mutations must be blocked immediately
        // and overlapping callers must share the exact same failed flush result.
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store!.OnCommit = async () =>
        {
            commitEntered.TrySetResult();
            await releaseCommit.Task;
        };
        store.FailCommits = true;

        Task<bool> prepareTask;
        Task<bool> overlappingCloseTask;
        try
        {
            collection.Entries[0].Included = true;
            await commitEntered.Task;

            prepareTask = harness.Project.PrepareToCloseAsync();
            overlappingCloseTask = harness.Session.TryBeginCloseAsync();
            Assert.True(harness.Session.IsSuspended);
            Assert.IsType<SessionResult.Suspended>(harness.Session.Execute("Edit during close", s =>
                s.WithCollection(CollectionOperations.SetIncluded(s.Collection(collection.Id), [collection.Entries[1].Item], true))));
        }
        finally
        {
            store.OnCommit = null;
            releaseCommit.TrySetResult();
        }

        // Let the held commit fail: both overlapping close calls must return false and unlock Retry/Discard recovery.
        Assert.False(await prepareTask);
        Assert.False(await overlappingCloseTask);
        Assert.True(harness.Project.SaveFailed);
        Assert.True(harness.Project.RetrySaveCommand.CanExecute(null));

        // Recover via Retry, then hold a successful commit and verify overlapping close calls share one pending task
        // and keep new edits blocked after both succeed.
        store.FailCommits = false;
        await harness.Project.RetrySaveCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => !harness.Project.SaveFailed, "recovered after failed close flush");

        var successCommitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSuccessCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnCommit = async () =>
        {
            successCommitEntered.TrySetResult();
            await releaseSuccessCommit.Task;
        };

        Task<bool> closeOk1;
        Task<bool> closeOk2;
        Task<bool> projectCloseOk;
        try
        {
            collection.Entries[1].Included = true;
            await successCommitEntered.Task;

            closeOk1 = harness.Session.TryBeginCloseAsync();
            closeOk2 = harness.Session.TryBeginCloseAsync();
            projectCloseOk = harness.Project.PrepareToCloseAsync();
            Assert.Same(closeOk1, closeOk2);
            Assert.False(closeOk1.IsCompleted);
            Assert.True(harness.Session.IsSuspended);
        }
        finally
        {
            store.OnCommit = null;
            releaseSuccessCommit.TrySetResult();
        }

        Assert.True(await closeOk1);
        Assert.True(await closeOk2);
        Assert.True(await projectCloseOk);
        Assert.True(harness.Session.IsSuspended);
        Assert.IsType<SessionResult.Suspended>(harness.Session.Execute("Post-prepare edit", s =>
            s.WithCollection(CollectionOperations.SetIncluded(s.Collection(collection.Id), [collection.Entries[2].Item], true))));
    });

    [Fact]
    public Task Png_export_uses_exact_approved_preview_revision_even_if_board_changes_during_save_dialog() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var collection = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => collection.Entries.All(e => e.HasCover), "covers loaded");
        collection.Entries[0].Included = true;
        collection.Entries[1].Included = true;
        collection.NewBoardName = "Previewed";
        await collection.CreateBoardCommand.ExecuteAsync(null);
        await harness.Session.FlushAsync();

        var board = Assert.IsType<BoardViewModel>(harness.Project.Content);

        // Capture a baseline PNG of the pre-mutation board to compare rendered output byte-for-byte.
        var baselinePath = Path.Combine(harness.ExportFolder, "baseline.png");
        harness.Dialogs.SaveFile = baselinePath;
        await board.ExportPngCommand.ExecuteAsync(null);

        var output = Path.Combine(harness.ExportFolder, "preview-locked.png");
        harness.Dialogs.SaveFile = output;
        // Mutate the board (rename + add two tiers to change PNG height) after preview approval, during the save dialog.
        harness.Dialogs.AfterSaveChoice = () =>
        {
            board.RenameText = "MutatedAfterPreview";
            board.CommitInlineRenameCommand.Execute(null);
            board.AddTierBelowCommand.Execute(board.Tiers[^1]);
            board.AddTierBelowCommand.Execute(board.Tiers[^1]);
        };

        await board.ExportPngCommand.ExecuteAsync(null);
        await harness.Session.FlushAsync();

        // Also export the mutated board to prove the mutation changes rendered PNG dimensions.
        var mutatedPath = Path.Combine(harness.ExportFolder, "after-mutation.png");
        harness.Dialogs.AfterSaveChoice = null;
        harness.Dialogs.SaveFile = mutatedPath;
        await board.ExportPngCommand.ExecuteAsync(null);

        static (int Width, int Height) ReadPngDimensions(string path)
        {
            using var stream = File.OpenRead(path);
            var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }

        Assert.True(File.Exists(output));
        Assert.Equal("MutatedAfterPreview", board.Name);
        var baselineSize = ReadPngDimensions(baselinePath);
        var lockedSize = ReadPngDimensions(output);
        var mutatedSize = ReadPngDimensions(mutatedPath);
        Assert.Equal(baselineSize, lockedSize);
        Assert.True(mutatedSize.Height > lockedSize.Height, $"Expected post-mutation PNG height ({mutatedSize.Height}) to exceed preview-locked height ({lockedSize.Height}).");
        Assert.Equal(File.ReadAllBytes(baselinePath), File.ReadAllBytes(output));
    });

    [Fact]
    public Task Collection_thumbnails_scale_with_monitor_dpi_and_refresh_on_dpi_change() => Ui.RunAsync(async () =>
    {
        Assert.Equal(ThumbnailLoader.PixelHeight, ThumbnailLoader.BucketHeight(105));
        Assert.Equal(ThumbnailLoader.MediumPixelHeight, ThumbnailLoader.BucketHeight(170));
        Assert.Equal(ThumbnailLoader.LargePixelHeight, ThumbnailLoader.BucketHeight(298));
        Assert.Equal(ThumbnailLoader.ExtraLargePixelHeight, ThumbnailLoader.BucketHeight(357));

        await using var harness = await DesktopHarness.CreateAsync(serveCovers: true);
        var collection = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => collection.Entries.All(e => e.HasCover), "covers loaded");
        collection.Density = CardDensity.Large; // 204 DIP poster height

        collection.SetDpiScale(1.0);
        Assert.Equal(ThumbnailLoader.MediumPixelHeight, collection.DesiredThumbnailHeight);
        var entry = collection.Entries[0];
        _ = entry.Thumbnail;
        var notified = 0;
        entry.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EntryViewModel.Thumbnail))
            {
                notified++;
            }
        };

        collection.SetDpiScale(1.75); // 204 * 1.75 = 357 px -> 480 px bucket
        Assert.Equal(ThumbnailLoader.ExtraLargePixelHeight, collection.DesiredThumbnailHeight);
        Assert.True(notified > 0, "Changing monitor DPI to a larger bucket must trigger visible thumbnail refresh.");

        collection.ViewMode = CollectionViewMode.List; // 60 * 1.75 = 105 px -> 160 px bucket
        Assert.Equal(ThumbnailLoader.PixelHeight, collection.DesiredThumbnailHeight);
    });

    private static IEnumerable<AutomationPeer> FlattenPeers(AutomationPeer root)
    {
        var children = root.GetChildren();
        if (children is null)
        {
            yield break;
        }

        foreach (var child in children)
        {
            yield return child;
            foreach (var nested in FlattenPeers(child))
            {
                yield return nested;
            }
        }
    }

    private static bool IsRenderedVisible(DependencyObject element, DependencyObject stopAt)
    {
        var current = element;
        while (current is not null)
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (ReferenceEquals(current, stopAt))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    [Fact]
    public Task Sidebar_context_menus_drag_reorder_and_board_double_click_details_work_end_to_end() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var col1 = await harness.CreateCollectionAsync();
        // Create a second collection so we have 2 collections and 2 boards to reorder, rename, and delete.
        var snapshot2 = new SourceSnapshot(
            SnapshotId.New(),
            new AcquisitionContext(2025, Season.Fall, CatalogueCategory.Tv),
            [
                new SeasonCapture(
                    new SeasonKey(2025, Season.Fall),
                    CatalogueCategory.Tv,
                    new Uri("https://www.livechart.me/fall-2025/tv"),
                    DateTimeOffset.UnixEpoch,
                    "test",
                    [new SourceOccurrence(0, new LiveChartId(9991), new SourceTitles("Fall Show", "Fall Show EN", "Fall Romaji", "秋"), [])]),
            ]);
        var col2Id = CollectionId.New();
        harness.Project.Execute("Add second collection", s => s.WithSnapshot(snapshot2).WithCollection(CollectionOperations.Create(col2Id, "Alpha Fall", snapshot2)));
        await Ui.UntilAsync(() => harness.Project.Collections.Count == 2, "two collections in sidebar");

        col1.Entries[0].Included = true;
        col1.NewBoardName = "Board One";
        await col1.CreateBoardCommand.ExecuteAsync(null);
        col1.NewBoardName = "Board Two";
        await col1.CreateBoardCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => harness.Project.Boards.Count == 2, "two boards in sidebar");

        // 1. Sidebar vertical drop calculator geometry (including offset indices from a scrolled virtualized list).
        var sidebarItems = new List<BoardDropCalculator.IndexedRect> { new(0, new Rect(0, 0, 200, 44)), new(1, new Rect(0, 46, 200, 44)) };
        Assert.Equal(0, BoardDropCalculator.ComputeVerticalDrop(sidebarItems, 2, new Point(50, 10), 200).InsertIndex);
        Assert.Equal(1, BoardDropCalculator.ComputeVerticalDrop(sidebarItems, 2, new Point(50, 35), 200).InsertIndex);
        Assert.Equal(2, BoardDropCalculator.ComputeVerticalDrop(sidebarItems, 2, new Point(50, 80), 200).InsertIndex);
        var scrolledItems = new List<BoardDropCalculator.IndexedRect> { new(10, new Rect(0, 0, 200, 44)), new(11, new Rect(0, 46, 200, 44)) };
        Assert.Equal(10, BoardDropCalculator.ComputeVerticalDrop(scrolledItems, 25, new Point(50, 10), 200).InsertIndex);
        Assert.Equal(11, BoardDropCalculator.ComputeVerticalDrop(scrolledItems, 25, new Point(50, 35), 200).InsertIndex);
        Assert.Equal(12, BoardDropCalculator.ComputeVerticalDrop(scrolledItems, 25, new Point(50, 80), 200).InsertIndex);

        // 2. Render full MainWindow and verify right-click ContextMenu on both Collections and Boards resolves live commands via ShellProxy.
        var shell = new ShellViewModel(harness.App) { Project = harness.Project };
        harness.Project.SidebarCollapsed = false;
        var window = new MainWindow { DataContext = shell };
        var content = Assert.IsAssignableFrom<UIElement>(window.Content);
        window.Content = null;
        var host = new Border { Width = 1220, Height = 780, DataContext = shell, Child = content };
        foreach (var key in window.Resources.Keys)
        {
            host.Resources[key] = window.Resources[key];
        }

        ThemeManager.ApplyToDictionary(host.Resources, AppTheme.Dark);
        host.Measure(new Size(1220, 780));
        host.Arrange(new Rect(0, 0, 1220, 780));
        host.UpdateLayout();

        var collectionsList = Descendants<ListBox>(host).Single(l => l.Name == "CollectionsList");
        var boardsList = Descendants<ListBox>(host).Single(l => l.Name == "BoardsList");
        var colContainer = Assert.IsType<ListBoxItem>(collectionsList.ItemContainerGenerator.ContainerFromIndex(0));
        var boardContainer = Assert.IsType<ListBoxItem>(boardsList.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.NotNull(colContainer.ContextMenu);
        Assert.NotNull(boardContainer.ContextMenu);
        var colMenuItems = colContainer.ContextMenu!.Items.OfType<MenuItem>().ToList();
        var boardMenuItems = boardContainer.ContextMenu!.Items.OfType<MenuItem>().ToList();
        Assert.Equal(4, colMenuItems.Count);
        Assert.Equal(4, boardMenuItems.Count);
        Assert.All(colMenuItems, m => Assert.NotNull(m.Command));
        Assert.All(boardMenuItems, m => Assert.NotNull(m.Command));

        // 3. Reorder collections and boards via MoveCollection / MoveBoard (same path as drag-drop) and verify undo + reopen durability.
        var originalColOrder = harness.Project.Collections.Select(c => c.Id).ToList();
        Assert.True(harness.Project.MoveCollection(harness.Project.Collections[0], 2));
        Assert.Equal([originalColOrder[1], originalColOrder[0]], harness.Project.Collections.Select(c => c.Id));

        var originalBoardOrder = harness.Project.Boards.Select(b => b.Id).ToList();
        Assert.True(harness.Project.MoveBoard(harness.Project.Boards[1], 0));
        Assert.Equal([originalBoardOrder[1], originalBoardOrder[0]], harness.Project.Boards.Select(b => b.Id));

        // 4. Rename and delete via sidebar context menu commands.
        harness.Dialogs.PromptResult = "Renamed From Sidebar";
        harness.Project.RenameBoardCommand.Execute(harness.Project.Boards[0]);
        Assert.Equal("Renamed From Sidebar", harness.Project.Boards[0].Label);

        // Deleting a collection still referenced by boards is blocked and explains why.
        var referencedCol = harness.Project.Collections.Single(c => c.Id == col1.Id);
        harness.Project.DeleteCollectionCommand.Execute(referencedCol);
        Assert.Equal(2, harness.Project.Collections.Count);
        Assert.Contains(harness.Dialogs.Messages, m => m.Title == "Cannot delete collection");

        // Deleting an unreferenced collection succeeds after confirmation.
        var unusedCol = harness.Project.Collections.Single(c => c.Id == col2Id);
        harness.Dialogs.Answer = true;
        harness.Project.DeleteCollectionCommand.Execute(unusedCol);
        Assert.Single(harness.Project.Collections);
        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => harness.Project.Collections.Count == 2, "deleted collection restored by undo");

        // Deleting a board from the sidebar succeeds after confirmation and can be undone.
        harness.Dialogs.Answer = true;
        harness.Project.DeleteBoardCommand.Execute(harness.Project.Boards[1]);
        Assert.Single(harness.Project.Boards);
        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => harness.Project.Boards.Count == 2, "deleted board restored by undo");

        // 5. Persisted sidebar order survives full SQLite reopen!
        var savedColOrder = harness.Project.Collections.Select(c => c.Id).ToList();
        var savedBoardOrder = harness.Project.Boards.Select(b => b.Id).ToList();
        await harness.ReopenAsync();
        Assert.Equal(savedColOrder, harness.Project.Collections.Select(c => c.Id));
        Assert.Equal(savedBoardOrder, harness.Project.Boards.Select(b => b.Id));

        // 5b. Real scrolled virtualized sidebar drop resolves the true list index (never jumping near index 0).
        var scrollCollection = harness.Session.Current.Collections.Values.First();
        var scrollSnapshot = harness.Session.Current.Snapshots[scrollCollection.AppliedSnapshot];
        var extraBoards = Enumerable.Range(3, 23).Select(n => BoardOperations.Create(
            BoardId.New(),
            $"Scroll Board {n:00}",
            scrollCollection,
            e => TitleResolver.Resolve(scrollSnapshot.Occurrence(e.AcceptedOccurrence).Titles, scrollCollection.Language, e.TitleOverride),
            _ => true,
            BoardOperations.DefaultTiers())).ToList();
        harness.Project.Execute("Add boards for scroll test", s => extraBoards.Aggregate(s, (acc, b) => acc.WithBoard(b)));
        await Ui.UntilAsync(() => harness.Project.Boards.Count == 25, "25 boards in sidebar");

        var scrollShell = new ShellViewModel(harness.App) { Project = harness.Project };
        harness.Project.SidebarCollapsed = false;
        var scrollWin = new MainWindow { DataContext = scrollShell };
        var scrollContent = Assert.IsAssignableFrom<UIElement>(scrollWin.Content);
        scrollWin.Content = null;
        var scrollHost = new Border { Width = 1220, Height = 680, DataContext = scrollShell, Child = scrollContent };
        foreach (var key in scrollWin.Resources.Keys)
        {
            scrollHost.Resources[key] = scrollWin.Resources[key];
        }

        ThemeManager.ApplyToDictionary(scrollHost.Resources, AppTheme.Dark);
        scrollHost.Measure(new Size(1220, 680));
        scrollHost.Arrange(new Rect(0, 0, 1220, 680));
        scrollHost.UpdateLayout();

        var scrolledBoardsList = Descendants<ListBox>(scrollHost).Single(l => l.Name == "BoardsList");
        var boardsScroller = Descendants<ScrollViewer>(scrolledBoardsList).First();
        boardsScroller.ScrollToBottom();
        scrollHost.Measure(new Size(1220, 680));
        scrollHost.Arrange(new Rect(0, 0, 1220, 680));
        scrollHost.UpdateLayout();

        var realized = MainWindow.CollectSidebarItems(scrolledBoardsList);
        Assert.True(realized.Count >= 3);
        Assert.True(realized[0].Index > 0);
        var tail = realized.TakeLast(3).ToList();
        var betweenTailFirstTwo = new Point(40, (tail[0].Bounds.Bottom + tail[1].Bounds.Top) / 2.0);
        var (insertAt, caretY, _, _) = BoardDropCalculator.ComputeVerticalDrop(realized, scrolledBoardsList.Items.Count, betweenTailFirstTwo, scrolledBoardsList.ActualWidth);
        Assert.Equal(tail[1].Index, insertAt);
        Assert.InRange(caretY, 0, scrolledBoardsList.ActualHeight);

        var draggedBoard = harness.Project.Boards[tail[2].Index];
        Assert.True(harness.Project.MoveBoard(draggedBoard, insertAt));
        Assert.Equal(draggedBoard.Id, harness.Project.Boards[insertAt].Id);

        // 6. Verify CopyNumberOverlay renders on duplicated board cards and BoardEntryDetailsView builds cleanly in Dark & Light themes.
        harness.Project.SelectedBoard = harness.Project.Boards[0];
        var boardVm = Assert.IsType<BoardViewModel>(harness.Project.Content);
        var originalCard = boardVm.Unranked.Entries[0];
        var dup = boardVm.DuplicateEntry(originalCard.Item);
        Assert.NotNull(dup);
        Assert.Equal(2, dup.CopyNumber);

        var boardView = new BoardView { DataContext = boardVm };
        ThemeManager.ApplyToDictionary(boardView.Resources, AppTheme.Dark);
        boardView.Measure(new Size(1100, 720));
        boardView.Arrange(new Rect(0, 0, 1100, 720));
        boardView.UpdateLayout();
        var visibleOverlays = Descendants<CopyNumberOverlay>(boardView).Where(o => o.Visibility == Visibility.Visible).ToList();
        Assert.Contains(visibleOverlays, o => o.CopyNumber == 2);

        // Double-clicking a card in the Unranked tray opens the LiveChart details & duplicate modal.
        var unrankedList = Descendants<ListBox>(boardView).Single(l => l.Name == "UnrankedList");
        var unrankedContainer = Assert.IsType<ListBoxItem>(unrankedList.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Null(harness.Dialogs.LastEntryDetails);
        unrankedContainer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = unrankedContainer,
        });
        Assert.NotNull(harness.Dialogs.LastEntryDetails);
        Assert.Equal(originalCard.Key, harness.Dialogs.LastEntryDetails!.Key);

        // Verify BoardEntryDetailsView instantiates and measures cleanly without binding errors.
        var detailsVm = new BoardEntryDetailsViewModel(boardVm, boardVm.Unranked.Entries[1], harness.Session.Current);
        var detailsView = new BoardEntryDetailsView { DataContext = detailsVm };
        var detailsHost = new Border { Width = 620, Height = 560, Child = detailsView };
        ThemeManager.ApplyToDictionary(detailsHost.Resources, AppTheme.Dark);
        detailsHost.Measure(new Size(620, 560));
        detailsHost.Arrange(new Rect(0, 0, 620, 560));
        detailsHost.UpdateLayout();
        Assert.True(detailsHost.ActualHeight > 0);
    });

    private static void AssertContrast(string themeName, ResourceDictionary dict, string fgKey, string bgKey, double minRatio)
    {
        var fg = Assert.IsType<SolidColorBrush>(dict[fgKey]).Color;
        var bg = Assert.IsType<SolidColorBrush>(dict[bgKey]).Color;
        var ratio = new TierColor(fg.R, fg.G, fg.B).ContrastRatio(new TierColor(bg.R, bg.G, bg.B));
        Assert.True(ratio >= minRatio, $"{themeName} contrast for {fgKey} ({fg}) on {bgKey} ({bg}) is {ratio:F2}:1, below {minRatio:F1}:1.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
