using System.IO;
using System.Windows;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Desktop.ViewModels;
using AnimeTierlist.Desktop.Views;
using AnimeTierlist.Infrastructure.Portable;
using Microsoft.Win32;

namespace AnimeTierlist.Desktop;

/// <summary>Composition root. Connects Infrastructure implementations, theme services, and view models.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "Services are disposed in OnExit, the end of the application's lifetime.")]
public partial class App : System.Windows.Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataRoot = ResolveDataRoot();
        if (dataRoot is null)
        {
            Shutdown(1);
            return;
        }

        _services = new AppServices(dataRoot);
        var themes = new ThemeManager();
        _services.Themes = themes;
        themes.Apply(_services.Preferences.Current.Theme);

        var window = new MainWindow();
        themes.AttachWindow(window);
        _services.Dialogs = new WpfDialogService(window, window.Overlays, themes, _services.VideoPlayer);
        window.DataContext = new ShellViewModel(_services);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Uses data/ beside the executable, or asks for a writable root for this session only.</summary>
    private static string? ResolveDataRoot()
    {
        var defaultRoot = PortableDataRoot.DefaultFor(AppContext.BaseDirectory);
        if (PortableDataRoot.TryProbeWritable(defaultRoot, out var failure))
        {
            return defaultRoot;
        }

        while (true)
        {
            var answer = MessageBox.Show(
                $"The program folder is not writable, so projects cannot be stored in:\n{defaultRoot}\n\n({failure})\n\n" +
                "Choose another folder for this session? The choice is not remembered; you will be asked again next time.",
                "AnimeTierlist: choose a data folder", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                return null;
            }

            var dialog = new OpenFolderDialog { Title = "Choose a writable data folder" };
            if (dialog.ShowDialog() == true && PortableDataRoot.TryProbeWritable(Path.Combine(dialog.FolderName, PortableDataRoot.FolderName), out failure))
            {
                return Path.Combine(dialog.FolderName, PortableDataRoot.FolderName);
            }
        }
    }
}
