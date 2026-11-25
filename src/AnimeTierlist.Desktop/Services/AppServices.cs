using System.IO;
using System.Reflection;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Infrastructure.Assets;
using AnimeTierlist.Infrastructure.Http;
using AnimeTierlist.Infrastructure.LiveChart;
using AnimeTierlist.Infrastructure.Portable;

namespace AnimeTierlist.Desktop.Services;

/// <summary>Application-lifetime services, created once at the composition root.</summary>
public sealed class AppServices : IDisposable
{
    public AppServices(string dataRoot)
        : this(dataRoot, http: null)
    {
    }

    /// <summary>Creates services with an optional custom HTTP client; null uses the default production handler.</summary>
    internal AppServices(string dataRoot, SourceHttpClient? http)
    {
        DataRoot = dataRoot;
        ProjectsFolder = PortableDataRoot.ProjectsFolder(dataRoot);
        Directory.CreateDirectory(ProjectsFolder);
        Version = typeof(AppServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "dev";

        // The app always identifies itself with its own product token and never imitates a browser.
        Http = http ?? new SourceHttpClient(SourceHttpClient.CreateHandler(), SourceHttpPolicy.Default, $"AnimeTierlist/{Version}", TimeProvider.System);
        Catalogue = new LiveChartCatalogueSource(Http, TimeProvider.System);
        AnimeDetails = new LiveChartAnimeDetailsSource(Http, TimeProvider.System);
        Preferences = new UiPreferencesService(dataRoot);
        VideoPlayer = new VideoPlayerEnvironment(dataRoot, AppContext.BaseDirectory);
    }

    public string DataRoot { get; }

    public string ProjectsFolder { get; }

    public string Version { get; }

    public SourceHttpClient Http { get; }

    public ICatalogueSource Catalogue { get; }

    /// <summary>On-demand fetcher for per-anime LiveChart videos, metadata, and AniList/MyAnimeList links.</summary>
    public IAnimeDetailsSource AnimeDetails { get; }

    /// <summary>Lazily created WebView2 environment for the in-app YouTube player.</summary>
    public VideoPlayerEnvironment VideoPlayer { get; }

    /// <summary>Portable UI settings persisted inside <see cref="DataRoot"/>.</summary>
    public UiPreferencesService Preferences { get; }

    /// <summary>Runtime theme manager wired by the composition root.</summary>
    public ThemeManager? Themes { get; set; }

    /// <summary>Validated season pages kept for resume during this session only.</summary>
    public CatalogueStaging Staging { get; } = new();

    /// <summary>Set by the composition root once the main window exists.</summary>
    public IDialogService? Dialogs { get; set; }

    public ProjectServices ForProject(string projectDirectory)
    {
        var assets = new AssetStore(projectDirectory);
        return new ProjectServices(assets, new ThumbnailLoader(new ThumbnailCache(projectDirectory, assets)), new LiveChartCoverSource(Http, assets, ImageLimits.Default));
    }

    public void Dispose()
    {
        Preferences.Dispose();
        Themes?.Dispose();
        Http.Dispose();
    }
}

/// <summary>Services bound to one open project folder.</summary>
public sealed record ProjectServices(AssetStore Assets, ThumbnailLoader Thumbnails, LiveChartCoverSource Covers);
