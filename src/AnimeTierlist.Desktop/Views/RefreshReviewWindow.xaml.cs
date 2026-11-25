using System.Windows;

namespace AnimeTierlist.Desktop.Views;

public partial class RefreshReviewWindow : Window
{
    public RefreshReviewWindow()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
    }

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
}
