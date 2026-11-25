using System.Windows.Controls;
using System.Windows.Input;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Views;

/// <summary>View mechanics for the LiveChart acquisition screen: Up/Down year stepping.</summary>
public partial class AcquisitionView : UserControl
{
    public AcquisitionView()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
    }

    private void OnYearKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not AcquisitionViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Up)
        {
            vm.IncrementYearCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            vm.DecrementYearCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is AcquisitionViewModel vm && vm.CreateCommand.CanExecute(null))
        {
            vm.CreateCommand.Execute(null);
            e.Handled = true;
        }
    }
}
