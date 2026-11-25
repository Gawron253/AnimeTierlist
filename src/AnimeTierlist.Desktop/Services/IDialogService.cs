using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Services;

/// <summary>A chosen output file. <see cref="ReplaceExisting"/> is true only when the user confirmed replacing an existing file.</summary>
public sealed record SaveFileChoice(string Path, bool ReplaceExisting);

/// <summary>Action chosen when attempting to close the window while a save has failed.</summary>
public enum FailedSaveCloseAction
{
    Cancel,
    Retry,
    Discard,
}

/// <summary>Modal interactions requested by view models; implemented by the WPF views.</summary>
public interface IDialogService
{
    /// <summary>Shows the reviewed refresh. Returns true only when the user applies it.</summary>
    bool ShowRefreshReview(RefreshReviewViewModel review);

    /// <summary>Shows the reviewed board update. Returns true only when the user applies it.</summary>
    bool ShowBoardUpdate(BoardUpdateViewModel review);

    /// <summary>Shows the PNG export scope and pre-flight layout preview. Returns true to proceed to the file picker.</summary>
    bool ShowPngExportOptions(PngExportPreviewViewModel preview) => true;

    /// <summary>Shows the LiveChart details and duplication dialog for a board anime card.</summary>
    void ShowBoardEntryDetails(BoardEntryDetailsViewModel details)
    {
    }

    /// <summary>Opens a verified HTTPS external page (LiveChart, AniList, MyAnimeList, or video link) in the user's default browser.</summary>
    void OpenExternalUrl(Uri url)
    {
    }

    /// <summary>Shows the sandboxed in-app YouTube player for one video.</summary>
    void ShowVideoPlayer(VideoPlayerViewModel player)
    {
    }

    /// <summary>Asks for a single validated text value (such as a new board name or rename). Null when cancelled.</summary>
    string? PromptText(string title, string description, string fieldLabel, string initialValue, string confirmButtonText, string? note = null) => null;

    /// <summary>Shows the keyboard shortcuts reference dialog.</summary>
    void ShowShortcuts(string initialSection)
    {
    }

    /// <summary>Asks what to do when closing the app with a failed save.</summary>
    FailedSaveCloseAction ChooseFailedSaveCloseAction(string failureMessage) => FailedSaveCloseAction.Cancel;

    /// <summary>Opens a local folder in Windows Explorer.</summary>
    void OpenFolderInExplorer(string folderPath)
    {
    }

    /// <summary>Asks a yes/no question with "No" as the default. Returns true only for an explicit yes.</summary>
    bool Confirm(string title, string message);

    /// <summary>Shows a message the user acknowledges, with an optional full list in a scrollable, copyable box.</summary>
    void Inform(string title, string message, IReadOnlyList<string>? details = null);

    /// <summary>Asks for a file to write. Replacing an existing file is confirmed by the dialog itself. Null when cancelled.</summary>
    SaveFileChoice? ChooseSaveFile(string title, string filter, string suggestedName);

    /// <summary>Asks for an existing file to read. Null when cancelled.</summary>
    string? ChooseOpenFile(string title, string filter);

    /// <summary>Asks for an existing folder. Null when cancelled.</summary>
    string? ChooseFolder(string title);
}
