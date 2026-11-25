using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Infrastructure.Archives;
using AnimeTierlist.Infrastructure.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeTierlist.Desktop.ViewModels;

/// <summary>Lightweight picker entry for one local project folder, read without opening SQLite.</summary>
public sealed record ProjectSummaryItem(string Name, string LastSavedText, string FolderPath)
{
    /// <summary>Screen readers and UI Automation announce the project folder name.</summary>
    public override string ToString() => Name;
}

/// <summary>Top-level state: the project list, theme/preferences controls, and the one open project.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private const string PickerPromptStatus = "Create a project or open an existing one.";

    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private Task<bool>? _inFlightClose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(FooterStatus))]
    private ProjectViewModel? _project;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenProjectCommand))]
    private string? _selectedProjectName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenProjectCommand))]
    private ProjectSummaryItem? _selectedProjectItem;

    [ObservableProperty]
    private string _projectSearchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateProjectCommand))]
    [NotifyPropertyChangedFor(nameof(NewProjectValidationNote), nameof(HasNewProjectValidationError))]
    private string _newProjectName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterStatus))]
    private string _status = PickerPromptStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    private AppTheme _theme;

    /// <summary>Initializes the top-level shell view model and scans the portable projects directory.</summary>
    public ShellViewModel(AppServices services)
    {
        _services = services;
        _theme = services.Preferences.Current.Theme;
        services.Preferences.SaveFailed += OnPreferencesSaveFailed;
        ProjectsView = CollectionViewSource.GetDefaultView(Projects);
        ProjectsView.Filter = MatchesProjectFilter;
        RefreshProjectList();
    }

    /// <summary>Single unified status message shown in the bottom bar so shell and project status never conflict.</summary>
    public string FooterStatus
    {
        get
        {
            if (Project is { } open)
            {
                if (!string.IsNullOrWhiteSpace(open.Status))
                {
                    return open.Status;
                }

                return string.Equals(Status, PickerPromptStatus, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(Status)
                    ? $"Project '{open.Name}' · {open.SaveText}"
                    : Status;
            }

            return Status;
        }
    }

    public string ProjectsFolder => _services.ProjectsFolder;

    public string Version => _services.Version;

    public bool HasProject => Project is not null;

    public bool HasProjects => Projects.Count > 0;

    public bool ShowProjectSearch => Projects.Count > 8;

    /// <summary>Alphabetical list of project folder names (preserved for workflow tests and compatibility).</summary>
    public ObservableCollection<string> ProjectNames { get; } = [];

    /// <summary>Rich project entries with last-saved timestamps read from the database file timestamp.</summary>
    public ObservableCollection<ProjectSummaryItem> Projects { get; } = [];

    /// <summary>Filtered view over <see cref="Projects"/>.</summary>
    public ICollectionView ProjectsView { get; }

    public string Title => Project is null ? "AnimeTierlist" : $"{Project.Name} - AnimeTierlist";

    public string ThemeLabel => Theme switch
    {
        AppTheme.Dark => "Theme: Dark",
        AppTheme.Light => "Theme: Light",
        _ => "Theme: System",
    };

    /// <summary>Live preview of the sanitized folder name or collision warning for <see cref="NewProjectName"/>.</summary>
    public string NewProjectValidationNote
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NewProjectName))
            {
                return string.Empty;
            }

            var folder = ProjectFolderName.From(NewProjectName);
            if (folder is null)
            {
                return "Enter a name containing at least one letter or digit.";
            }

            if (Directory.Exists(Path.Combine(_services.ProjectsFolder, folder)))
            {
                return $"A project named '{folder}' already exists.";
            }

            return !string.Equals(folder, NewProjectName.Trim(), StringComparison.Ordinal)
                ? $"Folder name: {folder}"
                : string.Empty;
        }
    }

    public bool HasNewProjectValidationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NewProjectName))
            {
                return false;
            }

            var folder = ProjectFolderName.From(NewProjectName);
            return folder is null || Directory.Exists(Path.Combine(_services.ProjectsFolder, folder));
        }
    }

    internal AppServices Services => _services;

    [RelayCommand(CanExecute = nameof(CanCreateProject))]
    private async Task CreateProjectAsync()
    {
        var folder = ProjectFolderName.From(NewProjectName);
        if (folder is null)
        {
            Status = "Enter a project name that contains letters or digits.";
            return;
        }

        var path = Path.Combine(_services.ProjectsFolder, folder);
        if (Directory.Exists(path))
        {
            Status = $"A project folder named '{folder}' already exists.";
            return;
        }

        try
        {
            var store = await SqliteProjectStore.CreateAsync(path, CancellationToken.None);
            await OpenAsync(folder, store);
            NewProjectName = string.Empty;
            RefreshProjectList();
        }
        catch (Exception ex) when (ex is ProjectStoreException or IOException or UnauthorizedAccessException)
        {
            Status = $"The project could not be created: {ex.Message}";
        }
    }

    private bool CanCreateProject() => !string.IsNullOrWhiteSpace(NewProjectName) && !HasNewProjectValidationError;

    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task OpenProjectAsync(string? name)
    {
        name ??= SelectedProjectName ?? SelectedProjectItem?.Name;
        if (name is null)
        {
            return;
        }

        try
        {
            var store = await SqliteProjectStore.OpenAsync(Path.Combine(_services.ProjectsFolder, name), CancellationToken.None);
            await OpenAsync(name, store);
        }
        catch (Exception ex) when (ex is ProjectStoreException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Status = $"'{name}' could not be opened: {ex.Message}";
        }
    }

    private bool CanOpenProject(string? name) => !string.IsNullOrWhiteSpace(name ?? SelectedProjectName ?? SelectedProjectItem?.Name);

    /// <summary>Closes the open project. Returns false when unsaved changes remain and the caller should not close.</summary>
    public async Task<bool> CloseProjectAsync()
    {
        if (Project is not { } project)
        {
            return true;
        }

        if (!await project.PrepareToCloseAsync())
        {
            return false;
        }

        project.PropertyChanged -= OnProjectPropertyChanged;
        string? closeError = null;
        try
        {
            await project.DisposeAsync();
        }
        catch (Exception ex)
        {
            closeError = ex.Message;
        }
        finally
        {
            Project = null;
            OnPropertyChanged(nameof(Title));
            RefreshProjectList();
        }

        Status = closeError is null ? "Project closed." : "Project closed with cleanup warning: " + closeError;
        return true;
    }

    /// <summary>
    /// Interactive close flow shared by both the top-bar Close button and window Alt+F4:
    /// deduplicates concurrent close requests and presents Retry / Discard / Cancel when a save failed.
    /// </summary>
    public Task<bool> CloseProjectWithRecoveryAsync()
    {
        if (_inFlightClose is { IsCompleted: false } existing)
        {
            return existing;
        }

        var task = CloseProjectWithRecoveryCoreAsync();
        _inFlightClose = task;
        return task;
    }

    private async Task<bool> CloseProjectWithRecoveryCoreAsync()
    {
        try
        {
            if (await CloseProjectAsync())
            {
                return true;
            }

            if (Project is not { } project)
            {
                return true;
            }

            var action = _services.Dialogs?.ChooseFailedSaveCloseAction(project.SaveFailureDetail) ?? FailedSaveCloseAction.Cancel;
            if (action == FailedSaveCloseAction.Retry && await project.RetrySaveForCloseAsync())
            {
                return await CloseProjectAsync();
            }

            if (action == FailedSaveCloseAction.Discard)
            {
                await project.DiscardForCloseAsync();
                return await CloseProjectAsync();
            }

            return false;
        }
        finally
        {
            _inFlightClose = null;
        }
    }

    [RelayCommand]
    private async Task CloseAsync() => await CloseProjectWithRecoveryAsync();

    /// <summary>Cycles the visual theme through Dark, Light, and System, persisting the choice inside data/.</summary>
    [RelayCommand]
    private void CycleTheme()
    {
        var next = Theme switch
        {
            AppTheme.Dark => AppTheme.Light,
            AppTheme.Light => AppTheme.System,
            _ => AppTheme.Dark,
        };
        SetTheme(next);
    }

    /// <summary>Sets an explicit visual theme and updates portable settings.</summary>
    [RelayCommand]
    private void SetTheme(AppTheme next)
    {
        Theme = next;
        _services.Preferences.Update(s => s with { Theme = next });
        _services.Themes?.Apply(next);
        Status = $"Theme set to {next}.";
    }

    /// <summary>Opens the keyboard shortcuts reference sheet for the active view.</summary>
    [RelayCommand]
    private void ShowShortcuts()
    {
        var section = Project?.Content switch
        {
            BoardViewModel => "Board",
            CollectionViewModel => "Collection",
            _ => "Global",
        };
        _services.Dialogs?.ShowShortcuts(section);
    }

    /// <summary>Opens the local portable projects folder in Windows Explorer.</summary>
    [RelayCommand]
    private void OpenProjectsFolder() => _services.Dialogs?.OpenFolderInExplorer(_services.ProjectsFolder);

    /// <summary>Imports a .animerank archive as a new project folder and opens it. Existing projects are never touched.</summary>
    [RelayCommand]
    private async Task ImportProjectAsync()
    {
        if (_services.Dialogs?.ChooseOpenFile("Import project", "AnimeTierlist project (*.animerank)|*.animerank") is not { } archive)
        {
            return;
        }

        var baseName = ProjectFolderName.From(Path.GetFileNameWithoutExtension(archive)) ?? "Imported project";
        var name = baseName;
        for (var n = 2; Directory.Exists(Path.Combine(_services.ProjectsFolder, name)); n++)
        {
            name = $"{baseName} ({n})";
        }

        try
        {
            await ProjectArchiveImporter.ImportAsync(archive, Path.Combine(_services.ProjectsFolder, name), ArchiveLimits.Default, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ProjectArchiveException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"The archive was not imported: {ex.Message}";
            _services.Dialogs?.Inform("Project not imported", ex.Message);
            return;
        }

        RefreshProjectList();
        await OpenProjectAsync(name);
    }

    partial void OnSelectedProjectItemChanged(ProjectSummaryItem? value)
    {
        if (value?.Name != SelectedProjectName)
        {
            SelectedProjectName = value?.Name;
        }
    }

    partial void OnSelectedProjectNameChanged(string? value)
    {
        if (SelectedProjectItem?.Name != value)
        {
            SelectedProjectItem = value is null ? null : Projects.FirstOrDefault(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
        }
    }

    partial void OnProjectSearchTextChanged(string value) => ProjectsView.Refresh();

    private bool MatchesProjectFilter(object item)
    {
        if (string.IsNullOrWhiteSpace(ProjectSearchText) || item is not ProjectSummaryItem project)
        {
            return true;
        }

        return project.Name.Contains(ProjectSearchText.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    private async Task OpenAsync(string name, SqliteProjectStore store)
    {
        if (!await CloseProjectAsync())
        {
            await store.DisposeAsync();
            return;
        }

        var session = await ProjectSession.OpenAsync(store, CancellationToken.None);
        var next = new ProjectViewModel(name, session, _services, _services.ForProject(store.ProjectDirectory), store);
        next.PropertyChanged += OnProjectPropertyChanged;
        Project = next;
        OnPropertyChanged(nameof(Title));
        Status = $"Opened '{name}'.";
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectViewModel.Status) or nameof(ProjectViewModel.SaveText))
        {
            OnPropertyChanged(nameof(FooterStatus));
        }
    }

    private void OnPreferencesSaveFailed(object? sender, string message)
    {
        if (_dispatcher.CheckAccess())
        {
            Status = message;
        }
        else
        {
            _ = _dispatcher.BeginInvoke(() => Status = message);
        }
    }

    private void RefreshProjectList()
    {
        var previous = SelectedProjectName;
        ProjectNames.Clear();
        Projects.Clear();
        ProjectSummaryItem? mostRecent = null;
        var latestWrite = DateTime.MinValue;
        foreach (var directory in Directory.EnumerateDirectories(_services.ProjectsFolder).Order(StringComparer.CurrentCultureIgnoreCase))
        {
            var dbPath = Path.Combine(directory, SqliteProjectStore.DatabaseFileName);
            if (!File.Exists(dbPath))
            {
                continue;
            }

            var name = Path.GetFileName(directory);
            var writeTime = File.GetLastWriteTime(dbPath);
            var saved = writeTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            var item = new ProjectSummaryItem(name, $"Last saved {saved}", directory);
            ProjectNames.Add(name);
            Projects.Add(item);
            if (writeTime >= latestWrite)
            {
                latestWrite = writeTime;
                mostRecent = item;
            }
        }

        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(ShowProjectSearch));
        ProjectsView.Refresh();

        if (previous is not null && Projects.FirstOrDefault(p => string.Equals(p.Name, previous, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            SelectedProjectItem = existing;
        }
        else if (SelectedProjectItem is null && mostRecent is not null)
        {
            SelectedProjectItem = mostRecent;
        }
    }
}

/// <summary>Turns a display name into a safe Windows folder name inside the projects folder.</summary>
public static class ProjectFolderName
{
    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    public static string? From(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimEnd('.', ' ');
        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80].TrimEnd('.', ' ');
        }

        if (!cleaned.Any(char.IsLetterOrDigit))
        {
            return null;
        }

        return Reserved.Contains(cleaned.Split('.')[0], StringComparer.OrdinalIgnoreCase) ? "_" + cleaned : cleaned;
    }
}
