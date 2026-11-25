using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Views;

/// <summary>View mechanics for the project picker: responsive two-column stacking and Enter/double-click actions.</summary>
public partial class ProjectPickerView : UserControl
{
    public ProjectPickerView()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 740;
        if (stacked)
        {
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            SpacerColumn.Width = new GridLength(0);
            RightColumn.Width = new GridLength(0);
            StackedSpacerRow.Height = new GridLength(12);
            Grid.SetRow(CreateCard, 2);
            Grid.SetColumn(CreateCard, 0);
        }
        else
        {
            LeftColumn.Width = new GridLength(1.35, GridUnitType.Star);
            SpacerColumn.Width = new GridLength(16);
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
            StackedSpacerRow.Height = new GridLength(0);
            Grid.SetRow(CreateCard, 0);
            Grid.SetColumn(CreateCard, 2);
        }
    }

    private void OnProjectDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Shell?.OpenProjectCommand.CanExecute(null) == true)
        {
            Shell.OpenProjectCommand.Execute(null);
        }
    }

    private void OnProjectListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Shell?.OpenProjectCommand.CanExecute(null) == true)
        {
            Shell.OpenProjectCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnNewProjectKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Shell?.CreateProjectCommand.CanExecute(null) == true)
        {
            Shell.CreateProjectCommand.Execute(null);
            e.Handled = true;
        }
    }
}
