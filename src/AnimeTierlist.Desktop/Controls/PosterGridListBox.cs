using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AnimeTierlist.Desktop.Controls;

/// <summary>
/// A <see cref="ListBox"/> specialized for the virtualized poster grid whose automation peers keep
/// realized item children (title, ordinal, cover image, and Included checkbox) reliably attached to the
/// <see cref="ListBoxItemAutomationPeer"/> across keyboard focus, selection, and reorder transitions.
/// </summary>
public sealed class PosterGridListBox : ListBox
{
    protected override DependencyObject GetContainerForItemOverride() => new PosterGridListBoxItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is PosterGridListBoxItem;

    protected override AutomationPeer OnCreateAutomationPeer() => new PosterGridListBoxAutomationPeer(this);
}

/// <summary>
/// Container for <see cref="PosterGridListBox"/> that refreshes its linked
/// <see cref="PosterGridListBoxItemAutomationPeer"/> after selection and focus transitions so UIA clients
/// never observe a stale empty child cache on the active card.
/// </summary>
public sealed class PosterGridListBoxItem : ListBoxItem
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PosterGridListBoxItemWrapperAutomationPeer(this);

    /// <summary>Refreshes the linked <see cref="ItemAutomationPeer"/> children cache when attached.</summary>
    internal void InvalidateItemAutomationChildren()
    {
        if (UIElementAutomationPeer.FromElement(this) is { EventsSource: { } itemPeer })
        {
            itemPeer.ResetChildrenCache();
        }
    }

    protected override void OnSelected(RoutedEventArgs e)
    {
        base.OnSelected(e);
        ScheduleAutomationRefresh();
    }

    protected override void OnUnselected(RoutedEventArgs e)
    {
        base.OnUnselected(e);
        ScheduleAutomationRefresh();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        ScheduleAutomationRefresh();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ScheduleAutomationRefresh();
    }

    private void ScheduleAutomationRefresh()
    {
        // Only refresh after layout/container moves have settled; never synchronously mid-selection/mid-move.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, InvalidateItemAutomationChildren);
    }
}

/// <summary>Automation peer for <see cref="PosterGridListBox"/> creating resilient item peers.</summary>
internal sealed class PosterGridListBoxAutomationPeer(PosterGridListBox owner) : ListBoxAutomationPeer(owner)
{
    protected override ItemAutomationPeer CreateItemAutomationPeer(object item) =>
        new PosterGridListBoxItemAutomationPeer(item, this);
}

/// <summary>
/// Wrapper peer that avoids re-parenting visual descendants away from the linked <see cref="ItemAutomationPeer"/>
/// once <see cref="AutomationPeer.EventsSource"/> is attached.
/// </summary>
internal sealed class PosterGridListBoxItemWrapperAutomationPeer(ListBoxItem owner) : ListBoxItemWrapperAutomationPeer(owner)
{
    protected override List<AutomationPeer>? GetChildrenCore() =>
        EventsSource is not null ? null : base.GetChildrenCore();
}

/// <summary>
/// Item peer that collects visible child automation peers directly from the realized container's visual tree
/// whenever available, preventing wrapper/item peer cache races from returning empty descendants.
/// </summary>
internal sealed class PosterGridListBoxItemAutomationPeer(object item, SelectorAutomationPeer selectorAutomationPeer)
    : ListBoxItemAutomationPeer(item, selectorAutomationPeer)
{
    protected override List<AutomationPeer>? GetChildrenCore()
    {
        if (ItemsControlAutomationPeer?.Owner is ItemsControl owner
            && owner.ItemContainerGenerator.ContainerFromItem(Item) is UIElement container)
        {
            var result = new List<AutomationPeer>();
            CollectChildPeers(container, result);
            if (result.Count > 0)
            {
                return result;
            }
        }

        return base.GetChildrenCore();
    }

    private static void CollectChildPeers(DependencyObject parent, List<AutomationPeer> result)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement uiElement)
            {
                if (uiElement.Visibility != Visibility.Visible)
                {
                    continue;
                }

                if (UIElementAutomationPeer.CreatePeerForElement(uiElement) is { } peer)
                {
                    result.Add(peer);
                    continue;
                }
            }

            CollectChildPeers(child, result);
        }
    }
}
