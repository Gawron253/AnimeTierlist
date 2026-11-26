using System.Diagnostics;
using System.IO;
using System.Windows;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Views;

/// <summary>
/// WPF modal dialog service. Anime details and the video player open as layers inside the main window, so sharing
/// that window (for example in Discord) shows them. Other dialogs are owned windows with the active theme, native
/// dark title bars, and owner bounds clamping.
/// </summary>
public sealed class WpfDialogService(Window owner, OverlayHost overlays, ThemeManager? themes = null, VideoPlayerEnvironment? videoPlayer = null) : IDialogService
{
    public bool ShowRefreshReview(RefreshReviewViewModel review) =>
        Prepare(new RefreshReviewWindow { DataContext = review }).ShowDialog() == true;

    public bool ShowBoardUpdate(BoardUpdateViewModel review) =>
        Prepare(new BoardUpdateWindow { DataContext = review }).ShowDialog() == true;

    public bool ShowPngExportOptions(PngExportPreviewViewModel preview) =>
        Prepare(new PngExportDialog { DataContext = preview }).ShowDialog() == true;

    public void ShowBoardEntryDetails(BoardEntryDetailsViewModel details) =>
        overlays.Show(new BoardEntryDetailsView { DataContext = details });

    public void ShowVideoPlayer(VideoPlayerViewModel player) =>
        overlays.Show(new VideoPlayerView(player, videoPlayer));

    public void OpenExternalUrl(Uri url)
    {
        if (ExternalUrlPolicy.IsAllowedBrowserLaunchUrl(url))
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
    }

    public string? PromptText(string title, string description, string fieldLabel, string initialValue, string confirmButtonText, string? note = null)
    {
        var dialog = Prepare(new TextPromptWindow());
        dialog.Configure(title, description, fieldLabel, initialValue, confirmButtonText, note);
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public void ShowShortcuts(string initialSection) =>
        Prepare(new ShortcutsWindow(initialSection)).ShowDialog();

    public FailedSaveCloseAction ChooseFailedSaveCloseAction(string failureMessage)
    {
        var dialog = Prepare(new AppDialogWindow());
        dialog.ConfigureFailedSaveClose(failureMessage);
        _ = dialog.ShowDialog();
        return dialog.CloseAction;
    }

    public void OpenFolderInExplorer(string folderPath)
    {
        if (Directory.Exists(folderPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });
        }
    }

    public bool Confirm(string title, string message)
    {
        var dialog = Prepare(new AppDialogWindow());
        var (destructive, confirmLabel) = title switch
        {
            _ when title.Contains("Delete", StringComparison.OrdinalIgnoreCase) => (true, "Delete"),
            _ when title.Contains("Discard", StringComparison.OrdinalIgnoreCase) => (true, "Discard"),
            _ when title.Contains("Remove", StringComparison.OrdinalIgnoreCase) => (true, "Remove"),
            _ when title.Contains("Mark cover missing", StringComparison.OrdinalIgnoreCase) => (true, "Mark missing"),
            _ => (false, "Confirm"),
        };
        dialog.ConfigureConfirm(title, message, destructive, confirmLabel);
        return dialog.ShowDialog() == true;
    }

    public void Inform(string title, string message, IReadOnlyList<string>? details = null)
    {
        var dialog = Prepare(new AppDialogWindow());
        dialog.ConfigureInform(title, message, details);
        _ = dialog.ShowDialog();
    }

    public SaveFileChoice? ChooseSaveFile(string title, string filter, string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = suggestedName, OverwritePrompt = true, AddExtension = true };
        // The dialog asked to confirm replacement only if the file existed when it closed.
        return dialog.ShowDialog(owner) == true ? new SaveFileChoice(dialog.FileName, File.Exists(dialog.FileName)) : null;
    }

    public string? ChooseOpenFile(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public string? ChooseFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    private T Prepare<T>(T dialog)
        where T : Window
    {
        dialog.Owner = owner;
        themes?.AttachWindow(dialog);
        // Clamp dialogs so they stay usable inside compact 960x540 DIP displays.
        var maxHeight = Math.Max(360, SystemParameters.WorkArea.Height * 0.88);
        var maxWidth = Math.Max(420, SystemParameters.WorkArea.Width * 0.92);
        dialog.MaxHeight = Math.Min(dialog.MaxHeight, maxHeight);
        dialog.MaxWidth = Math.Min(dialog.MaxWidth, maxWidth);
        if (!double.IsNaN(dialog.Height) && dialog.Height > dialog.MaxHeight)
        {
            dialog.Height = dialog.MaxHeight;
        }

        if (!double.IsNaN(dialog.Width) && dialog.Width > dialog.MaxWidth)
        {
            dialog.Width = dialog.MaxWidth;
        }

        return dialog;
    }
}
