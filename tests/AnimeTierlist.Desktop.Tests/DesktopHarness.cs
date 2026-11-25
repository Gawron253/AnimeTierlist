using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows.Threading;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Desktop.ViewModels;
using AnimeTierlist.Infrastructure.Http;
using AnimeTierlist.Infrastructure.LiveChart;
using AnimeTierlist.Infrastructure.Storage;
using AnimeTierlist.Infrastructure.Tests.Http;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace AnimeTierlist.Desktop.Tests;

/// <summary>
/// Runs a test on a dedicated STA thread with a running WPF dispatcher, as the view models expect. Awaits inside the
/// test resume on that dispatcher.
/// </summary>
internal static class Ui
{
    public static Task RunAsync(Func<Task> test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await test();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
            if (failure is not null)
            {
                done.SetException(failure);
            }
            else
            {
                done.SetResult();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    /// <summary>Lets queued dispatcher work (session refreshes, background checks) run until the condition holds.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(20);
        }
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool Answer { get; set; }

    public List<string> Questions { get; } = [];

    public bool ShowRefreshReview(RefreshReviewViewModel review) => false;

    /// <summary>Lets a test choose additions, removals, and presentation; returns <see cref="ApplyBoardUpdate"/>.</summary>
    public Action<BoardUpdateViewModel>? ReviewBoardUpdate { get; set; }

    public bool ApplyBoardUpdate { get; set; } = true;

    public bool ShowBoardUpdate(BoardUpdateViewModel review)
    {
        ReviewBoardUpdate?.Invoke(review);
        return ApplyBoardUpdate;
    }

    public bool Confirm(string title, string message)
    {
        Questions.Add(title);
        return Answer;
    }

    public List<(string Title, string Message)> Messages { get; } = [];

    public string? SaveFile { get; set; }

    public string? OpenFile { get; set; }

    public string? Folder { get; set; }

    public List<string> FileDialogTitles { get; } = [];

    /// <summary>Text returned by <see cref="PromptText"/> when non-null.</summary>
    public string? PromptResult { get; set; }

    /// <summary>Last details view model passed to <see cref="ShowBoardEntryDetails"/>.</summary>
    public BoardEntryDetailsViewModel? LastEntryDetails { get; private set; }

    /// <summary>Optional test hook executed when <see cref="ShowBoardEntryDetails"/> is called.</summary>
    public Action<BoardEntryDetailsViewModel>? InspectEntryDetails { get; set; }

    /// <summary>External LiveChart URLs requested via <see cref="OpenExternalUrl"/>.</summary>
    public List<Uri> OpenedUrls { get; } = [];

    public string? PromptText(string title, string description, string fieldLabel, string initialValue, string confirmButtonText, string? note = null)
    {
        Questions.Add(title);
        return PromptResult;
    }

    public void ShowBoardEntryDetails(BoardEntryDetailsViewModel details)
    {
        LastEntryDetails = details;
        InspectEntryDetails?.Invoke(details);
    }

    public void OpenExternalUrl(Uri url) => OpenedUrls.Add(url);

    /// <summary>In-app players requested via <see cref="ShowVideoPlayer"/>.</summary>
    public List<VideoPlayerViewModel> ShownPlayers { get; } = [];

    public void ShowVideoPlayer(VideoPlayerViewModel player) => ShownPlayers.Add(player);

    public void Inform(string title, string message, IReadOnlyList<string>? details = null) =>
        Messages.Add((title, details is null ? message : message + "\n" + string.Join("\n", details)));

    /// <summary>Runs after the save dialog closes, for example to create the chosen file behind the user's back.</summary>
    public Action? AfterSaveChoice { get; set; }

    public SaveFileChoice? ChooseSaveFile(string title, string filter, string suggestedName)
    {
        FileDialogTitles.Add(title);
        var choice = SaveFile is null ? null : new SaveFileChoice(SaveFile, File.Exists(SaveFile));
        AfterSaveChoice?.Invoke();
        return choice;
    }

    public string? ChooseOpenFile(string title, string filter)
    {
        FileDialogTitles.Add(title);
        return OpenFile;
    }

    public string? ChooseFolder(string title)
    {
        FileDialogTitles.Add(title);
        return Folder;
    }
}

/// <summary>
/// One open project wired as the app wires it, except that HTTP goes to a scripted handler serving the frozen
/// LiveChart fixture and generated cover images. Nothing reaches the network.
/// </summary>
internal sealed class DesktopHarness : IAsyncDisposable
{
    public const string PageUrl = "https://www.livechart.me/summer-2025/movies";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "animetierlist-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceHttpClient _http;

    private DesktopHarness()
    {
        Time = new ManualTime();
        _http = new SourceHttpClient(Handler, SourceHttpPolicy.Default, "AnimeTierlist/0.1.0", Time, Time.DelayAsync, jitter: () => 0);
        App = new AppServices(_root, _http) { Dialogs = Dialogs };
        ProjectDirectory = Path.Combine(App.ProjectsFolder, "Test");
    }

    public ScriptedHandler Handler { get; } = new();

    public ManualTime Time { get; }

    public FakeDialogs Dialogs { get; } = new();

    public AppServices App { get; }

    public string ProjectDirectory { get; }

    /// <summary>An empty folder for export output, inside the harness root that is deleted afterwards.</summary>
    public string ExportFolder => Directory.CreateDirectory(Path.Combine(_root, "exports")).FullName;

    public ProjectSession Session { get; private set; } = null!;

    public ProjectViewModel Project { get; private set; } = null!;

    /// <summary>Cover bytes served per image URL; tests may change them between requests.</summary>
    public Dictionary<string, byte[]> CoverBytes { get; } = new(StringComparer.Ordinal);

    /// <param name="wrapStore">Optionally wraps the session's store, for example to hold commits.</param>
    public static async Task<DesktopHarness> CreateAsync(bool serveCovers = true, bool servePage = true, Func<IProjectStore, IProjectStore>? wrapStore = null)
    {
        var harness = new DesktopHarness();
        var store = await SqliteProjectStore.CreateAsync(harness.ProjectDirectory, CancellationToken.None);
        harness.Session = await ProjectSession.OpenAsync(wrapStore?.Invoke(store) ?? store, CancellationToken.None);
        harness.Project = new ProjectViewModel("Test", harness.Session, harness.App, harness.App.ForProject(harness.ProjectDirectory), store);
        if (servePage)
        {
            harness.Handler.Add(PageUrl, HttpStatusCode.OK, Fixture("summer-2025-movies.html"), "text/html");
        }

        if (serveCovers)
        {
            harness.ServeCovers();
        }

        return harness;
    }

    public static List<int> OracleIds()
    {
        using var json = JsonDocument.Parse(Fixture("summer-2025-movies.expected.json"));
        return json.RootElement.GetProperty("oracle").GetProperty("orderedIds").EnumerateArray().Select(e => e.GetInt32()).ToList();
    }

    public static byte[] Jpeg(byte shade)
    {
        using var bitmap = new SKBitmap(175, 250);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(shade, (byte)(255 - shade), 0x80));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>
    /// Fetches Summer 2025 Movies, creates the collection, and waits for its automatic cover run. A new collection
    /// includes every show; with <paramref name="excludeAll"/> the harness then runs Exclude all, so tests
    /// can include exactly the shows they need.
    /// </summary>
    public async Task<CollectionViewModel> CreateCollectionAsync(bool excludeAll = true)
    {
        var acquisition = Project.Acquisition;
        acquisition.YearText = "2025";
        acquisition.Season = acquisition.SeasonOptions.Single(o => o.Label == "Summer");
        acquisition.Category = acquisition.CategoryOptions.Single(o => o.Label == "Movies");
        await acquisition.FetchCommand.ExecuteAsync(null);
        Assert.True(acquisition.HasPreview, acquisition.Failure);
        acquisition.CreateCommand.Execute(null);
        var view = Assert.IsType<CollectionViewModel>(Project.Content);
        if (view.FetchCoversCommand.ExecutionTask is { } covers)
        {
            await covers;
        }

        await Ui.UntilAsync(() => view.Entries.Count == OracleIds().Count, "collection entries");

        // The cover file check holds originals open while hashing them; tests that damage covers must not race it.
        await Ui.UntilAsync(() => !view.IsCheckingCovers, "collection cover check");
        if (excludeAll)
        {
            view.ExcludeAllCommand.Execute(null);
            await Ui.UntilAsync(() => view.Entries.All(e => !e.Included) && Project.SaveText == "Saved", "Exclude all saved");
        }

        return view;
    }

    public CollectionState Collection(CollectionViewModel view) => Session.Current.Collection(view.Id);

    /// <summary>Closes the project as the app does and opens it again from disk (no undo history survives).</summary>
    public async Task ReopenAsync()
    {
        Assert.True(await Project.PrepareToCloseAsync());
        await Project.DisposeAsync();
        var store = await SqliteProjectStore.OpenAsync(ProjectDirectory, CancellationToken.None);
        Session = await ProjectSession.OpenAsync(store, CancellationToken.None);
        Project = new ProjectViewModel("Test", Session, App, App.ForProject(ProjectDirectory), store);
    }

    public async ValueTask DisposeAsync()
    {
        await Project.DisposeAsync();
        App.Dispose();
        SqliteConnection.ClearAllPools();

        // Disposing the project cancels background cover checks and thumbnail loads without awaiting them, so one can
        // still hold a cover file open for a moment. Wait for it to let go instead of failing the test in cleanup.
        await Ui.UntilAsync(TryDeleteRoot, "test folder deleted");
    }

    private bool TryDeleteRoot()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return true;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32)
        {
            return false;
        }
    }

    public void ServeCovers()
    {
        var page = LiveChartSeasonParser.Parse(System.Text.Encoding.UTF8.GetString(Fixture("summer-2025-movies.html")), new SeasonKey(2025, Season.Summer),
            CatalogueCategory.Movies, new Uri(PageUrl), DateTimeOffset.UnixEpoch);
        var capture = Assert.IsType<SeasonPageResult.Valid>(page).Capture;
        byte shade = 10;
        foreach (var candidate in capture.Occurrences.SelectMany(o => o.ImageCandidates))
        {
            var url = candidate.Url.AbsoluteUri;
            CoverBytes[url] = Jpeg(shade += 7);
            Handler.Add(url, () => ScriptedHandler.Response(HttpStatusCode.OK, CoverBytes[url], "image/jpeg"));
        }
    }

    private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "livechart", file));
}

/// <summary>Wraps the session's store so a test can hold commits until it releases them.</summary>
internal sealed class HeldCommitStore(IProjectStore inner) : IProjectStore
{
    private TaskCompletionSource _release = CompletedSource();

    /// <summary>Holds every later commit until the returned action runs.</summary>
    public Action Hold()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _release, release);
        return () => release.TrySetResult();
    }

    public Task<ProjectData> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);

    public async Task CommitAsync(ProjectChange change, CancellationToken cancellationToken)
    {
        await Volatile.Read(ref _release).Task.WaitAsync(cancellationToken);
        await inner.CommitAsync(change, cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }
}
