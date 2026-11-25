using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AnimeTierlist.Desktop.Controls;

/// <summary>Visual and screen-reader severity for <see cref="InfoBar"/>.</summary>
public enum InfoBarSeverity
{
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>
/// A themed inline status banner with accessible live-region announcements, optional action controls,
/// and optional dismissal.
/// </summary>
public partial class InfoBar : UserControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(InfoBarSeverity), typeof(InfoBar), new PropertyMetadata(InfoBarSeverity.Info, OnVisualPropertyChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(InfoBar), new PropertyMetadata(string.Empty, OnTextPropertyChanged));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(InfoBar), new PropertyMetadata(string.Empty, OnTextPropertyChanged));

    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent), typeof(object), typeof(InfoBar), new PropertyMetadata(null));

    public static readonly DependencyProperty IsClosableProperty = DependencyProperty.Register(
        nameof(IsClosable), typeof(bool), typeof(InfoBar), new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty DismissCommandProperty = DependencyProperty.Register(
        nameof(DismissCommand), typeof(ICommand), typeof(InfoBar), new PropertyMetadata(null));

    /// <summary>Initializes the InfoBar and applies its default visual state.</summary>
    public InfoBar()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
        ApplyVisuals();
    }

    public InfoBarSeverity Severity
    {
        get => (InfoBarSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public bool IsClosable
    {
        get => (bool)GetValue(IsClosableProperty);
        set => SetValue(IsClosableProperty, value);
    }

    public ICommand? DismissCommand
    {
        get => (ICommand?)GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is InfoBar bar)
        {
            bar.ApplyVisuals();
        }
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is InfoBar bar)
        {
            bar.UpdateTextVisibilityAndAnnounce();
        }
    }

    private void ApplyVisuals()
    {
        var (bgKey, borderKey, fgKey, iconKey, liveSetting) = Severity switch
        {
            InfoBarSeverity.Success => ("Brush.SuccessSurface", "Brush.SuccessBorder", "Brush.SuccessText", "Icon.Check", AutomationLiveSetting.Polite),
            InfoBarSeverity.Warning => ("Brush.WarningSurface", "Brush.WarningBorder", "Brush.WarningText", "Icon.Warning", AutomationLiveSetting.Polite),
            InfoBarSeverity.Danger => ("Brush.DangerSurface", "Brush.DangerBorder", "Brush.DangerText", "Icon.Warning", AutomationLiveSetting.Assertive),
            _ => ("Brush.AccentSurface", "Brush.AccentFill", "Brush.AccentText", "Icon.Info", AutomationLiveSetting.Polite),
        };

        BarBorder.SetResourceReference(Border.BackgroundProperty, bgKey);
        BarBorder.SetResourceReference(Border.BorderBrushProperty, borderKey);
        SeverityIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, fgKey);
        TitleText.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
        MessageText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        if (TryFindResource(iconKey) is Geometry geometry)
        {
            SeverityIcon.Data = geometry;
        }

        AutomationProperties.SetLiveSetting(this, liveSetting);
        DismissButton.Visibility = IsClosable ? Visibility.Visible : Visibility.Collapsed;
        UpdateTextVisibilityAndAnnounce();
    }

    private void UpdateTextVisibilityAndAnnounce()
    {
        TitleText.Visibility = string.IsNullOrWhiteSpace(Title) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Visibility = string.IsNullOrWhiteSpace(Message) ? Visibility.Collapsed : Visibility.Visible;
        if (IsVisible && (!string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Message)))
        {
            UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        if (DismissCommand?.CanExecute(null) == true)
        {
            DismissCommand.Execute(null);
        }
        else
        {
            Visibility = Visibility.Collapsed;
        }
    }
}
