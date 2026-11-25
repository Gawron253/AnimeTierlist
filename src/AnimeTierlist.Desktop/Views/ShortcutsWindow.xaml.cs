using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AnimeTierlist.Desktop.Views;

/// <summary>Themed keyboard shortcuts reference dialog that highlights and scrolls to the active workspace section.</summary>
public partial class ShortcutsWindow : Window
{
    private readonly string _initialSection;

    /// <summary>Creates a shortcuts reference window highlighting <paramref name="initialSection"/>.</summary>
    public ShortcutsWindow(string initialSection = "Global")
    {
        _initialSection = initialSection;
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
        ApplyActiveSectionHighlight();
        Loaded += OnLoaded;
    }

    /// <summary>Scrolls the highlighted section into view after layout completes.</summary>
    public void ScrollActiveSectionIntoView()
    {
        if (ActiveCard is not { } target)
        {
            return;
        }

        UpdateLayout();
        if (ReferenceEquals(target, BoardCard))
        {
            ShortcutsScroller.ScrollToBottom();
        }
        else if (ReferenceEquals(target, GlobalCard))
        {
            ShortcutsScroller.ScrollToTop();
        }
        else
        {
            target.BringIntoView(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        }
    }

    private Border? ActiveCard => _initialSection switch
    {
        "Board" => BoardCard,
        "Collection" => CollectionCard,
        _ => GlobalCard,
    };

    private void ApplyActiveSectionHighlight()
    {
        if (ActiveCard?.Child is StackPanel stack && stack.Children.Count > 0 && stack.Children[0] is TextBlock header)
        {
            var row = new DockPanel { Margin = header.Margin };
            header.Margin = new Thickness(0);
            stack.Children.RemoveAt(0);
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "CURRENT VIEW",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                },
            };
            badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSurface");
            badge.SetResourceReference(Border.BorderBrushProperty, "Brush.AccentFill");
            badge.BorderThickness = new Thickness(1);
            ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);
            row.Children.Add(header);
            stack.Children.Insert(0, row);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollActiveSectionIntoView);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.F1 or Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
