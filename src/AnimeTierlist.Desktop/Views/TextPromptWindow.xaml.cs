using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace AnimeTierlist.Desktop.Views;

/// <summary>Themed single-field modal prompt used for creating boards and contextual renames.</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>Trimmed text value entered by the user.</summary>
    public string Value => InputBox.Text.Trim();

    /// <summary>Populates labels, initial text, and optional scope note.</summary>
    public void Configure(string title, string description, string fieldLabel, string initialValue, string confirmButtonText, string? note)
    {
        Title = title;
        HeadingText.Text = title;
        DescriptionText.Text = description;
        FieldLabel.Content = fieldLabel;
        AutomationProperties.SetName(InputBox, fieldLabel.Replace("_", string.Empty, StringComparison.Ordinal));
        InputBox.Text = initialValue;
        ConfirmButton.Content = confirmButtonText;
        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(initialValue);
        if (!string.IsNullOrWhiteSpace(note))
        {
            NoteText.Text = note;
            NoteText.Visibility = Visibility.Visible;
        }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) =>
        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(InputBox.Text))
        {
            DialogResult = true;
        }
    }
}
