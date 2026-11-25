using System.Windows;

namespace AnimeTierlist.Desktop.Views;

public partial class BoardUpdateWindow : Window
{
    public BoardUpdateWindow()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
    }

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
}
