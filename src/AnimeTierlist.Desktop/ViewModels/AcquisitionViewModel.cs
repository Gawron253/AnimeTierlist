using System.Collections.ObjectModel;
using System.Globalization;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeTierlist.Desktop.ViewModels;

public sealed record SeasonOption(string Label, Season? Value)
{
    /// <summary>Screen readers and UI Automation announce the label, not the record shape.</summary>
    public override string ToString() => Label;
}

public sealed record CategoryOption(string Label, CatalogueCategory Value)
{
    /// <summary>Screen readers and UI Automation announce the label, not the record shape.</summary>
    public override string ToString() => Label;
}

/// <summary>Status of one constituent season during whole-year or single-season acquisition.</summary>
public enum SeasonStepState
{
    Pending,
    Requesting,
    Validated,
    Empty,
    Failed,
}

/// <summary>Progress pill for one season in the acquisition stepper.</summary>
public sealed partial class SeasonStepItem(Season season) : ObservableObject
{
    [ObservableProperty]
    private SeasonStepState _state = SeasonStepState.Pending;

    [ObservableProperty]
    private string _detail = "Pending";

    public Season Season { get; } = season;

    public string Label => Season.ToString();
}

/// <summary>Structured preview row preserving exact LiveChart popularity ordinal and season badge.</summary>
public sealed record PreviewEntryItem(int Ordinal, string Title, string SeasonTag, string DisplayLine)
{
    public override string ToString() => DisplayLine;
}

/// <summary>
/// New-collection workflow: choose a season or year and category, fetch LiveChart's popularity order, preview it,
/// and create a collection (every entry starts excluded). Nothing is saved until the user creates the collection.
/// </summary>
public sealed partial class AcquisitionViewModel : ObservableObject
{
    private readonly ProjectViewModel _project;
    private SourceSnapshot? _snapshot;
    private int _inputVersion;

    [ObservableProperty]
    private string _yearText = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWholeYear), nameof(IsIndeterminateProgress))]
    private SeasonOption _season;

    [ObservableProperty]
    private CategoryOption _category;

    [ObservableProperty]
    private string _progressText = "Choose a season or a whole year, then fetch it from LiveChart.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminateProgress))]
    private bool _isFetching;

    [ObservableProperty]
    private int _validatedSeasonCount;

    [ObservableProperty]
    private int _requiredSeasonCount = 1;

    [ObservableProperty]
    private string? _failure;

    [ObservableProperty]
    private string? _previewSummary;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _hasPreview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _collectionName = string.Empty;

    /// <summary>Initializes the acquisition view model with the current calendar season and TV category default.</summary>
    public AcquisitionViewModel(ProjectViewModel project)
    {
        _project = project;
        _season = SeasonOptions[1 + ((DateTime.Today.Month - 1) / 3)];
        _category = CategoryOptions[0];
        ResetSeasonSteps();
    }

    public bool IsWholeYear => Season.Value is null;

    /// <summary>Single-season fetches use indeterminate progress; whole-year fetches show determinate season progress.</summary>
    public bool IsIndeterminateProgress => IsFetching && !IsWholeYear;

    /// <summary>Re-evaluates creation availability when project save-suspension changes.</summary>
    internal void NotifyEditsSuspendedChanged() => CreateCommand.NotifyCanExecuteChanged();

    public IReadOnlyList<SeasonOption> SeasonOptions { get; } =
    [
        new("Whole year", null),
        new("Winter", Core.Season.Winter),
        new("Spring", Core.Season.Spring),
        new("Summer", Core.Season.Summer),
        new("Fall", Core.Season.Fall),
    ];

    public IReadOnlyList<CategoryOption> CategoryOptions { get; } =
    [
        new("TV", CatalogueCategory.Tv),
        new("Movies", CatalogueCategory.Movies),
        new("OVAs", CatalogueCategory.Ovas),
        new("All", CatalogueCategory.All),
    ];

    /// <summary>Per-season progress indicators for whole-year or single-season fetches.</summary>
    public ObservableCollection<SeasonStepItem> SeasonSteps { get; } = [];

    /// <summary>Structured preview items in LiveChart popularity order with ordinal badge and season tag.</summary>
    public ObservableCollection<PreviewEntryItem> PreviewEntries { get; } = [];

    /// <summary>Increases the year input by one.</summary>
    [RelayCommand]
    private void IncrementYear()
    {
        var current = int.TryParse(YearText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : DateTime.Today.Year;
        YearText = Math.Clamp(current + 1, 1950, 2100).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Decreases the year input by one.</summary>
    [RelayCommand]
    private void DecrementYear()
    {
        var current = int.TryParse(YearText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : DateTime.Today.Year;
        YearText = Math.Clamp(current - 1, 1950, 2100).ToString(CultureInfo.InvariantCulture);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task FetchAsync(CancellationToken cancellationToken)
    {
        ClearPreview();
        ResetSeasonSteps();
        var version = _inputVersion;
        AcquisitionContext context;
        try
        {
            if (!int.TryParse(YearText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            {
                Failure = "Enter the year as a number, for example 2025.";
                return;
            }

            context = new AcquisitionContext(year, Season.Value, Category.Value);
        }
        catch (ArgumentException ex)
        {
            Failure = ex.Message;
            return;
        }

        RequiredSeasonCount = context.Season is null ? 4 : 1;
        ValidatedSeasonCount = 0;
        IsFetching = true;

        var label = ProjectViewModel.Describe(context);
        var progress = new Progress<CatalogueProgress>(p =>
        {
            ValidatedSeasonCount = p.ValidatedSeasons;
            RequiredSeasonCount = p.RequiredSeasons;
            MarkStepRequesting(p.Season.Season, p.ValidatedSeasons);
            ProgressText = context.Season is null
                ? $"Requesting {p.Season.Season} {p.Season.Year} from LiveChart ({p.ValidatedSeasons} of {p.RequiredSeasons} seasons ready; LiveChart requires 5 seconds between pages)..."
                : $"Requesting {label} from LiveChart...";
        });

        try
        {
            var outcome = await CatalogueAcquisition.AcquireAsync(_project.App.Catalogue, _project.App.Staging, context, RetryBudget.CreateDefault(), progress, cancellationToken);
            if (version != _inputVersion)
            {
                // The year, season, or category changed while fetching; never show a preview for other inputs.
                ProgressText = "The selection changed while fetching. Fetch again to preview it.";
                return;
            }

            switch (outcome)
            {
                case CatalogueOutcome.Complete complete:
                    ValidatedSeasonCount = RequiredSeasonCount;
                    MarkStepsComplete(complete);
                    ShowPreview(complete, label);
                    break;
                case CatalogueOutcome.Incomplete incomplete:
                    ValidatedSeasonCount = incomplete.StagedSeasons.Length;
                    MarkStepsIncomplete(incomplete);
                    ProgressText = incomplete.StagedSeasons.IsEmpty
                        ? "Nothing was saved. Saved projects still work offline."
                        : $"Nothing was saved. {incomplete.StagedSeasons.Length} validated season(s) are kept in session staging, so fetching again resumes where this stopped.";
                    Failure = incomplete.IsPaused
                        ? $"LiveChart asked the app to wait longer than the retry budget allows. Fetch again to resume. ({incomplete.Detail})"
                        : incomplete.Failure == SeasonPageFailure.Unreachable
                            ? $"{await NetworkHint.DescribeUnreachableAsync()} ({incomplete.Detail})"
                            : $"{incomplete.FailedSeason.Season} {incomplete.FailedSeason.Year} could not be read: {Explain(incomplete.Failure)} ({incomplete.Detail})";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled. Validated seasons are kept for this session.";
        }
        finally
        {
            IsFetching = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var id = CollectionId.New();
        var name = CollectionName.Trim();
        if (_project.Execute($"Create collection '{name}'", s => s.WithSnapshot(snapshot).WithCollection(CollectionOperations.Create(id, name, snapshot))) is SessionResult.Accepted)
        {
            ClearPreview();
            ProgressText = "Collection created. Covers are downloading; every show starts included until you exclude it.";
            _project.ShowCollection(id);
            if (_project.Content is CollectionViewModel view)
            {
                view.FetchCoversCommand.Execute(null);
            }
        }
    }

    private bool CanCreate() => HasPreview && !string.IsNullOrWhiteSpace(CollectionName) && !_project.EditsSuspended;

    partial void OnYearTextChanged(string value) => InputsChanged();

    partial void OnSeasonChanged(SeasonOption value)
    {
        ResetSeasonSteps();
        InputsChanged();
    }

    partial void OnCategoryChanged(CategoryOption value) => InputsChanged();

    private void InputsChanged()
    {
        _inputVersion++;
        if (HasPreview)
        {
            ClearPreview();
            ProgressText = "The selection changed. Fetch again to preview it.";
        }
    }

    private void ResetSeasonSteps()
    {
        SeasonSteps.Clear();
        if (Season.Value is { } single)
        {
            SeasonSteps.Add(new SeasonStepItem(single));
        }
        else
        {
            SeasonSteps.Add(new SeasonStepItem(Core.Season.Winter));
            SeasonSteps.Add(new SeasonStepItem(Core.Season.Spring));
            SeasonSteps.Add(new SeasonStepItem(Core.Season.Summer));
            SeasonSteps.Add(new SeasonStepItem(Core.Season.Fall));
        }
    }

    private void MarkStepRequesting(Season active, int validatedCount)
    {
        var seenActive = false;
        for (var i = 0; i < SeasonSteps.Count; i++)
        {
            var step = SeasonSteps[i];
            if (step.Season == active)
            {
                step.State = SeasonStepState.Requesting;
                step.Detail = "Requesting...";
                seenActive = true;
            }
            else if (!seenActive && i < validatedCount)
            {
                step.State = SeasonStepState.Validated;
                step.Detail = "Ready";
            }
        }
    }

    private void MarkStepsComplete(CatalogueOutcome.Complete complete)
    {
        var emptySet = complete.VerifiedEmptySeasons.Select(s => s.Season).ToHashSet();
        var counts = complete.Snapshot.Captures.ToDictionary(c => c.Season.Season, c => c.Occurrences.Length);
        foreach (var step in SeasonSteps)
        {
            if (emptySet.Contains(step.Season))
            {
                step.State = SeasonStepState.Empty;
                step.Detail = "0 shows (verified empty)";
            }
            else
            {
                step.State = SeasonStepState.Validated;
                step.Detail = counts.TryGetValue(step.Season, out var n) ? $"{n} cards" : "Ready";
            }
        }
    }

    private void MarkStepsIncomplete(CatalogueOutcome.Incomplete incomplete)
    {
        var staged = incomplete.StagedSeasons.Select(s => s.Season).ToHashSet();
        foreach (var step in SeasonSteps)
        {
            if (step.Season == incomplete.FailedSeason.Season)
            {
                step.State = SeasonStepState.Failed;
                step.Detail = "Failed";
            }
            else if (staged.Contains(step.Season))
            {
                step.State = SeasonStepState.Validated;
                step.Detail = "Staged";
            }
        }
    }

    private void ShowPreview(CatalogueOutcome.Complete complete, string label)
    {
        _snapshot = complete.Snapshot;
        var index = 0;
        var showSeasonTag = complete.Snapshot.Captures.Length > 1;
        foreach (var item in complete.Snapshot.Sequence)
        {
            var title = complete.Snapshot.Occurrence(item.FirstOccurrence).Titles.Displayed;
            var line = $"{++index}. {title}";
            var tag = showSeasonTag ? item.FirstOccurrence.Season.Season.ToString() : string.Empty;
            PreviewEntries.Add(new PreviewEntryItem(index, title, tag, line));
        }

        var empty = complete.VerifiedEmptySeasons;
        PreviewSummary = $"{complete.UniqueItems} shows in LiveChart popularity order" +
            (complete.RawOccurrences != complete.UniqueItems ? $" ({complete.RawOccurrences} source cards; repeated shows keep their first position)" : string.Empty) +
            (empty.IsEmpty ? ". Every show starts included until you exclude it." : $". LiveChart lists no shows for: {string.Join(", ", empty.Select(s => s.Season))}. Every show starts included.");
        CollectionName = label;
        HasPreview = true;
        ProgressText = "Review the order, name the collection, and create it.";
    }

    private void ClearPreview()
    {
        _snapshot = null;
        PreviewEntries.Clear();
        PreviewSummary = null;
        Failure = null;
        HasPreview = false;
    }

    internal static string Explain(SeasonPageFailure failure) => failure switch
    {
        SeasonPageFailure.WrongPage => "LiveChart has no such page yet",
        SeasonPageFailure.ChangedLayout => "LiveChart's page layout changed, so the order cannot be trusted",
        SeasonPageFailure.WrongSortContext => "the page was not in popularity order",
        SeasonPageFailure.InvalidCard => "an entry had no usable LiveChart ID or title",
        SeasonPageFailure.TooLarge => "the page was larger than allowed",
        SeasonPageFailure.Paused => "requests are paused",
        SeasonPageFailure.Unreachable => "LiveChart could not be reached",
        _ => "the network request failed",
    };
}
