using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AnimeTierlist.Desktop.ViewModels;
using AnimeTierlist.Desktop.Views;

namespace AnimeTierlist.Desktop.Tests;

/// <summary>
/// Keyboard behavior of the real board view, laid out off-screen: keyboard focus on a show selects it, so the
/// row's key 1 binding moves the focused show, and the tier name box has an accessible name.
/// </summary>
public sealed class BoardViewKeyboardTests
{
    [Fact]
    public Task Keyboard_focus_on_a_show_selects_it_so_key_1_moves_that_show() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        foreach (var entry in collection.Entries.Take(3))
        {
            entry.Included = true;
        }

        collection.NewBoardName = "Keys";
        await collection.CreateBoardCommand.ExecuteAsync(null);
        var board = Assert.IsType<BoardViewModel>(harness.Project.Content);
        var view = new BoardView { DataContext = board };
        view.Measure(new Size(1200, 900));
        view.Arrange(new Rect(0, 0, 1200, 900));
        view.UpdateLayout();

        var second = board.Unranked.Entries[1];
        var item = Descendants<ListBoxItem>(view).Single(i => i.DataContext == second);
        Assert.NotSame(second, board.Unranked.SelectedEntry);

        // What Tab into the row raises on the show's container.
        item.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, item) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
        Assert.Same(second, board.Unranked.SelectedEntry);

        var row = ItemsControl.ItemsControlFromItemContainer(item);
        var key1 = row.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.D1 && b.Modifiers == ModifierKeys.None);
        Assert.True(key1.Command.CanExecute(key1.CommandParameter));
        key1.Command.Execute(key1.CommandParameter);
        await Ui.UntilAsync(() => board.Tiers[0].Entries.Any(e => e.Item == second.Item), "the focused show in the first tier");

        var tierName = (TextBox)view.FindName("TierNameBox");
        Assert.Equal("Tier name", UIElementAutomationPeer.CreatePeerForElement(tierName).GetName());
    });

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
