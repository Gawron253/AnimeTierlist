using System.Windows;

namespace AnimeTierlist.Desktop.Views;

/// <summary>Pre-flight PNG export scope and deterministic dimension preview dialog.</summary>
public partial class PngExportDialog : Window
{
    public PngExportDialog()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
    }

    private void OnContinue(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnOpenUpdateBoard(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.PngExportPreviewViewModel vm)
        {
            vm.RequestUpdateBoard();
        }

        DialogResult = false;
    }
}
