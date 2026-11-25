using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AnimeTierlist.Desktop.Services;

namespace AnimeTierlist.Desktop.Views;

/// <summary>
/// Themed replacement for unthemed system MessageBoxes: supports safe confirmations (defaulting focus to No/Cancel),
/// copyable detail lists, and three-way failed-save close recovery.
/// </summary>
public partial class AppDialogWindow : Window
{
    private string _detailsText = string.Empty;

    public AppDialogWindow()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
    }

    /// <summary>Result chosen when used for failed-save close recovery.</summary>
    public FailedSaveCloseAction CloseAction { get; private set; } = FailedSaveCloseAction.Cancel;

    /// <summary>Configures a confirmation dialog with safe default focus on Cancel and a clear action verb.</summary>
    public void ConfigureConfirm(string title, string message, bool destructive = false, string confirmLabel = "Confirm")
    {
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;

        var noButton = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 92, Margin = new Thickness(0, 0, 8, 0) };
        noButton.Click += (_, _) => DialogResult = false;

        var yesButton = new Button
        {
            Content = confirmLabel,
            MinWidth = 96,
            Style = (Style)FindResource(destructive ? "DangerPrimaryButton" : "PrimaryButton"),
        };
        yesButton.Click += (_, _) => DialogResult = true;

        ButtonBar.Children.Add(noButton);
        ButtonBar.Children.Add(yesButton);
        Loaded += (_, _) => noButton.Focus();
    }

    /// <summary>Configures an informational or error dialog with an optional copyable details list.</summary>
    public void ConfigureInform(string title, string message, IReadOnlyList<string>? details)
    {
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;

        if (details is { Count: > 0 })
        {
            _detailsText = string.Join(Environment.NewLine, details);
            DetailsBox.Text = _detailsText;
            DetailsBox.Visibility = Visibility.Visible;
            CopyButton.Visibility = Visibility.Visible;
            AutomationProperties.SetName(DetailsBox, $"{details.Count} affected shows");
        }

        var okButton = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 92, Style = (Style)FindResource("PrimaryButton") };
        okButton.Click += (_, _) => DialogResult = true;
        ButtonBar.Children.Add(okButton);
        Loaded += (_, _) => okButton.Focus();
    }

    /// <summary>Configures the three-way dialog shown when closing the app with a failed save.</summary>
    public void ConfigureFailedSaveClose(string failureMessage)
    {
        Title = "Unsaved changes";
        HeadingText.Text = "The last change could not be saved";
        MessageText.Text = string.IsNullOrWhiteSpace(failureMessage)
            ? "Retry saving before closing, discard your unsaved changes (restoring the last saved state and clearing undo history), or cancel closing."
            : $"{failureMessage}\n\nRetry saving before closing, discard unsaved changes (clearing undo history), or cancel closing.";

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { CloseAction = FailedSaveCloseAction.Cancel; DialogResult = false; };

        var discard = new Button { Content = "Discard changes", Style = (Style)FindResource("DangerButton"), Margin = new Thickness(0, 0, 8, 0) };
        discard.Click += (_, _) => { CloseAction = FailedSaveCloseAction.Discard; DialogResult = true; };

        var retry = new Button { Content = "Retry save", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        retry.Click += (_, _) => { CloseAction = FailedSaveCloseAction.Retry; DialogResult = true; };

        ButtonBar.Children.Add(cancel);
        ButtonBar.Children.Add(discard);
        ButtonBar.Children.Add(retry);
        Loaded += (_, _) => retry.Focus();
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_detailsText))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_detailsText);
            CopyButton.Content = "Copied";
        }
        catch (ExternalException)
        {
            CopyButton.Content = "Clipboard busy";
        }
    }
}
