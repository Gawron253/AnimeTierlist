using System.Net;
using System.Text;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Tests;

/// <summary>
/// Fetch details for a whole collection and filter it by the fetched details, through the real view models, session,
/// SQLite store, and LiveChart adapters with HTTP served from synthetic pages.
/// </summary>
public sealed class DetailsFilterWorkflowTests
{
    [Fact]
    public Task Fetch_details_checks_every_show_once_then_filters_and_excludes_shows_without_an_opening() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var view = await harness.CreateCollectionAsync(excludeAll: false);
        var ids = view.Entries.Select(e => e.Item.Value).ToList();
        var order = harness.Collection(view).Entries.Select(e => e.Item).ToList();
        Serve(harness, ids[0], hasOpening: true, source: "Manga", rating: "8.2");
        Serve(harness, ids[1], hasOpening: false, source: "Original", rating: "6.5");
        Serve(harness, ids[2], hasOpening: true, source: "Light Novel", rating: "7.6");

        Assert.Equal(ids.Count, view.MissingDetailsCount);
        Assert.All(view.Entries, e => Assert.Null(e.Facts.HasOpening));
        var undo = harness.Project.UndoLabel;

        await view.FetchDetailsCommand.ExecuteAsync(null);

        // Three shows were served; the rest answered 404 and are counted as failed, to retry later.
        Assert.False(view.IsFetchingDetails);
        Assert.StartsWith($"Details and videos fetched for 3 of {ids.Count} shows.", view.DetailsStatus, StringComparison.Ordinal);
        await Ui.UntilAsync(() => view.MissingDetailsCount == ids.Count - 3, "details applied to the view");
        Assert.Equal(undo, harness.Project.UndoLabel);
        Assert.Equal(order, harness.Collection(view).Entries.Select(e => e.Item));
        Assert.Equal([true, false, true], view.Entries.Take(3).Select(e => e.Facts.HasOpening));
        Assert.Equal(["Manga", "Original", "Light Novel"], view.Entries.Take(3).Select(e => e.Facts.SourceMaterial));

        // Filter options list what the fetched details contain, with counts.
        Assert.Contains(view.OpeningOptions, o => o.Value == "no" && o.Label == "No OP (1)");
        Assert.Contains(view.SourceOptions, o => o.Value == "Light Novel" && o.Label == "Light Novel (1)");
        Assert.Contains(view.GenreOptions, o => o.Value == "Fantasy");

        // Show only shows without an opening, then exclude them.
        view.OpeningFilter = "no";
        Assert.True(view.IsFiltered);
        Assert.False(view.CanReorder);
        Assert.Equal("Filters (1)", view.DetailFiltersLabel);
        Assert.Equal([ids[1]], Visible(view));
        view.ExcludeVisibleCommand.Execute(null);
        await Ui.UntilAsync(() => harness.Project.SaveText == "Saved", "exclusion saved");
        Assert.Equal([order[1]], harness.Collection(view).Entries.Where(e => !e.Included).Select(e => e.Item));
        Assert.Equal(order, harness.Collection(view).Entries.Select(e => e.Item));

        // Filters combine: shows with an opening, rated 8 or more.
        view.OpeningFilter = "yes";
        view.RatingFilter = "8.0";
        Assert.Equal([ids[0]], Visible(view));
        view.RatingFilter = FilterOption.AnyValue;
        view.SourceFilter = "Light Novel";
        Assert.Equal([ids[2]], Visible(view));

        // A second run asks LiveChart only about the shows still missing details. A selected value that no show has
        // afterwards stays selected at zero instead of silently widening the filter.
        view.OpeningFilter = FilterOption.AnyValue;
        view.SourceFilter = FilterOption.UnknownValue;
        Assert.Equal(ids.Count - 3, Visible(view).Count);
        Serve(harness, ids[3], hasOpening: true, source: "Manga", rating: "7.0");
        for (var i = 4; i < ids.Count; i++)
        {
            Serve(harness, ids[i], hasOpening: false, source: "Original", rating: "6.0");
        }

        var requests = harness.Handler.Requests.Count;
        await view.FetchDetailsCommand.ExecuteAsync(null);
        var again = harness.Handler.Requests.Skip(requests).Select(r => r.RequestUri!.AbsoluteUri).ToList();
        Assert.DoesNotContain(again, u => u.Contains($"/anime/{ids[0]}", StringComparison.Ordinal));
        Assert.Contains(again, u => u.EndsWith($"/anime/{ids[3]}/videos", StringComparison.Ordinal));
        await Ui.UntilAsync(() => view.MissingDetailsCount == 0, "all details applied");
        Assert.Equal(FilterOption.UnknownValue, view.SourceFilter);
        Assert.Contains(view.SourceOptions, o => o.Value == FilterOption.UnknownValue && o.Label == "Unknown (0)");
        Assert.Empty(Visible(view));

        view.ClearFiltersCommand.Execute(null);
        Assert.False(view.IsFiltered);
        Assert.Equal(ids.Count, Visible(view).Count);

        // Fetched details survive reopening the project.
        await harness.ReopenAsync();
        harness.Project.SelectedCollection = harness.Project.Collections.Single();
        var reopened = Assert.IsType<CollectionViewModel>(harness.Project.Content);
        await Ui.UntilAsync(() => reopened.Entries.Count == ids.Count, "reopened collection");
        Assert.Equal([true, false, true], reopened.Entries.Take(3).Select(e => e.Facts.HasOpening));
    });

    private static List<int> Visible(CollectionViewModel view) => view.View.Cast<EntryViewModel>().Select(e => e.Item.Value).ToList();

    private static void Serve(DesktopHarness harness, int id, bool hasOpening, string source, string rating)
    {
        var opening = hasOpening
            ? """
              <div class="lc-video" data-controller="video" data-video-id="1">
                  <a href="https://www.youtube.com/watch?v=opening0001" class="lc-interactive-surface__interaction-delegate"></a>
                  <div class="lcm3-text--title-md">OP</div>
              </div>
              """
            : string.Empty;
        var videos = $"""
            <!DOCTYPE html><html><head><link rel="canonical" href="https://www.livechart.me/anime/{id}/videos"></head><body>
            {opening}
            <div class="lc-video" data-controller="video" data-video-id="2">
                <a href="https://www.youtube.com/watch?v=ending00001" class="lc-interactive-surface__interaction-delegate"></a>
                <div class="lcm3-text--title-md">ED</div>
            </div>
            </body></html>
            """;
        var profile = $"""
            <!DOCTYPE html><html><body>
            <div data-controller="anime-details" data-anime-details-id="{id}">
                <div data-anime-details-target="ratingBar"><span data-rating-display-target="ratingA">{rating}</span></div>
                <div><div>Format</div><div><a href="/anime?anime_formats=2">Movie</a></div></div>
                <div><div>Source</div><div><a href="/tags/10">{source}</a></div></div>
                <div><a href="/studios/1">Studio {id}</a></div>
                <div><a href="/tags/1">Fantasy</a></div>
            </div>
            </body></html>
            """;
        harness.Handler.Add($"https://www.livechart.me/anime/{id}/videos", HttpStatusCode.OK, Encoding.UTF8.GetBytes(videos), "text/html");
        harness.Handler.Add($"https://www.livechart.me/anime/{id}", HttpStatusCode.OK, Encoding.UTF8.GetBytes(profile), "text/html");
    }
}
