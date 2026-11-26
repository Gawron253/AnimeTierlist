using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AnimeTierlist.Desktop.Views;

/// <summary>A view shown as a modal layer inside the main window by <see cref="OverlayHost"/>.</summary>
public interface IOverlayView
{
    /// <summary>Raised when the view asks to be dismissed (its Close button or a view-model request).</summary>
    event EventHandler? CloseRequested;

    /// <summary>Raised when <see cref="IsFullWindow"/> changes.</summary>
    event EventHandler? FullWindowChanged;

    /// <summary>True while the view fills the whole window, such as a video in full screen.</summary>
    bool IsFullWindow { get; }

    /// <summary>Largest size of the view's card. The card shrinks to fit smaller windows.</summary>
    Size MaxCardSize { get; }

    /// <summary>True when the card fills the available height; false when it sizes to its content.</summary>
    bool FillsHeight { get; }

    /// <summary>Called once, before the layer leaves the window, to release the view's resources.</summary>
    void OnOverlayClosed();
}

/// <summary>
/// Shows dialogs as modal layers inside the main window instead of separate windows, so window capture (such as
/// sharing the application in Discord) includes them. Only the top layer is visible; lower layers keep their
/// state. While any layer is open the <see cref="Workspace"/> is disabled, and closing a layer restores focus.
/// </summary>
public sealed class OverlayHost : Grid
{
    private const double CardMargin = 24;
    private readonly List<Layer> _layers = [];

    public OverlayHost()
    {
        Visibility = Visibility.Collapsed;
        var scrim = new Border();
        scrim.SetResourceReference(Border.BackgroundProperty, "Brush.BgOverlay");
        Children.Add(scrim);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Contained);
    }

    /// <summary>The window content disabled while a layer is open, so it takes no input behind the layer.</summary>
    public UIElement? Workspace { get; set; }

    public bool IsOpen => _layers.Count > 0;

    /// <summary>The visible layer's view, or null when nothing is open.</summary>
    public IOverlayView? Top => _layers.Count > 0 ? _layers[^1].View : null;

    /// <summary>Shows <paramref name="view"/> above any open layer.</summary>
    public void Show(IOverlayView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var element = view as FrameworkElement ?? throw new ArgumentException("The view must be a FrameworkElement.", nameof(view));
        var layer = new Layer(view, CreateCard(element), Keyboard.FocusedElement);
        if (_layers.Count > 0)
        {
            _layers[^1].Card.Visibility = Visibility.Collapsed;
        }

        _layers.Add(layer);
        view.CloseRequested += OnCloseRequested;
        view.FullWindowChanged += OnFullWindowChanged;
        ApplyCardLayout(layer);
        layer.Card.Loaded += OnCardLoaded;
        Children.Add(layer.Card);

        if (_layers.Count == 1)
        {
            Visibility = Visibility.Visible;
            if (Workspace is not null)
            {
                Workspace.IsEnabled = false;
            }
        }
    }

    /// <summary>Closes <paramref name="view"/> and any layer above it. Does nothing if it is not open.</summary>
    public void Close(IOverlayView view)
    {
        var index = _layers.FindIndex(l => ReferenceEquals(l.View, view));
        while (index >= 0 && _layers.Count > index)
        {
            CloseTop();
        }
    }

    /// <summary>Closes every layer, for example before the window closes or the project changes.</summary>
    public void CloseAll()
    {
        while (_layers.Count > 0)
        {
            CloseTop();
        }
    }

    /// <summary>
    /// Esc closes the top layer, except while it fills the window (the video handles its own full screen). With the
    /// video focused, WebView2 forwards Esc from a callback where the browser must not be disposed, so the layer
    /// closes after the key event returns, and only if it is still the top layer.
    /// </summary>
    public bool HandleEscape()
    {
        if (Top is not { } top)
        {
            return false;
        }

        if (!top.IsFullWindow)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (ReferenceEquals(Top, top))
                {
                    Close(top);
                }
            });
        }

        return true;
    }

    private void CloseTop()
    {
        var layer = _layers[^1];
        _layers.RemoveAt(_layers.Count - 1);
        layer.View.CloseRequested -= OnCloseRequested;
        layer.View.FullWindowChanged -= OnFullWindowChanged;
        layer.Card.Loaded -= OnCardLoaded;
        try
        {
            layer.View.OnOverlayClosed();
        }
        finally
        {
            Children.Remove(layer.Card);
            layer.Card.Child = null;
        }

        if (_layers.Count > 0)
        {
            _layers[^1].Card.Visibility = Visibility.Visible;
        }
        else
        {
            Visibility = Visibility.Collapsed;
            if (Workspace is not null)
            {
                Workspace.IsEnabled = true;
            }
        }

        if (layer.RestoreFocus is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } restore)
        {
            restore.Focus();
        }
    }

    private static Border CreateCard(FrameworkElement content)
    {
        var card = new Border { Child = content, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Stretch };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.BgCanvas");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderSubtle");
        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(card, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(card, KeyboardNavigationMode.Contained);
        return card;
    }

    private static void ApplyCardLayout(Layer layer)
    {
        var card = layer.Card;
        if (layer.View.IsFullWindow)
        {
            card.Margin = default;
            card.MaxWidth = double.PositiveInfinity;
            card.MaxHeight = double.PositiveInfinity;
            card.BorderThickness = default;
            card.CornerRadius = default;
            card.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            var max = layer.View.MaxCardSize;
            card.Margin = new Thickness(CardMargin);
            card.MaxWidth = max.Width;
            card.MaxHeight = max.Height;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(10);
            card.VerticalAlignment = layer.View.FillsHeight ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (sender is IOverlayView view)
        {
            Close(view);
        }
    }

    private void OnFullWindowChanged(object? sender, EventArgs e)
    {
        if (_layers.Find(l => ReferenceEquals(l.View, sender)) is { } layer)
        {
            ApplyCardLayout(layer);
        }
    }

    /// <summary>Moves keyboard focus into a newly shown card so keys go to the layer, not the content behind it.</summary>
    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border card)
        {
            card.Loaded -= OnCardLoaded;
            if (_layers.Count > 0 && ReferenceEquals(_layers[^1].Card, card) && !card.IsKeyboardFocusWithin)
            {
                card.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        }
    }

    private sealed record Layer(IOverlayView View, Border Card, IInputElement? RestoreFocus);
}
