using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Application.Covers;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;
using AnimeTierlist.Infrastructure.Http;
using AnimeTierlist.Infrastructure.LiveChart;
using AnimeTierlist.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Tests.LiveChart;

/// <summary>
/// Development-only live checks, skipped unless ANIMETIERLIST_LIVE_CHECK=1. Each acquires one small category (one
/// season, or the four seasons of a whole year) and its covers, following the documented host policy: catalogue
/// requests one at a time at least five seconds apart, at most two concurrent image requests. Never part of the default
/// automated suite.
/// </summary>
public sealed class LiveAcquisitionCheck : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "animetierlist-live", Guid.NewGuid().ToString("N"));

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Small_category_acquires_end_to_end_against_the_live_source()
    {
        await AcquireAsync(new AcquisitionContext(2025, Season.Summer, CatalogueCategory.Movies), "animetierlist-live-check.txt");
    }

    [Fact]
    public async Task Whole_year_acquires_end_to_end_in_calendar_order_against_the_live_source()
    {
        var context = new AcquisitionContext(2025, null, CatalogueCategory.Movies);
        var snapshot = await AcquireAsync(context, "animetierlist-live-year-check.txt");

        Assert.Equal([Season.Winter, Season.Spring, Season.Summer, Season.Fall], snapshot.Captures.Select(c => c.Season.Season));
        Assert.All(snapshot.Captures, c => Assert.Equal(LiveChartRoutes.SeasonPage(c.Season, CatalogueCategory.Movies), c.RequestUrl));
        Assert.All(snapshot.Captures, c => Assert.NotEmpty(c.Occurrences));

        // The collection sequence is every season in calendar order, each in its captured order, with a repeated ID
        // kept at its first occurrence.
        var expected = snapshot.Captures.SelectMany(c => c.Occurrences.Select(o => o.Item)).Distinct();
        Assert.Equal(expected, snapshot.Sequence.Select(s => s.Item));
    }

    private async Task<SourceSnapshot> AcquireAsync(AcquisitionContext context, string reportName)
    {
        if (Environment.GetEnvironmentVariable("ANIMETIERLIST_LIVE_CHECK") != "1")
        {
            Assert.Skip("Live source checks run only with ANIMETIERLIST_LIVE_CHECK=1.");
        }

        var ct = TestContext.Current.CancellationToken;
        var userAgent = Environment.GetEnvironmentVariable("ANIMETIERLIST_LIVE_USER_AGENT") ?? "dev-check/1.0";
        using var http = new SourceHttpClient(SourceHttpClient.CreateHandler(), SourceHttpPolicy.Default, userAgent, TimeProvider.System);

        var outcome = await CatalogueAcquisition.AcquireAsync(new LiveChartCatalogueSource(http, TimeProvider.System), new CatalogueStaging(), context, RetryBudget.CreateDefault(), null, ct);
        var complete = Assert.IsType<CatalogueOutcome.Complete>(outcome);
        Assert.True(complete.UniqueItems > 0);

        var project = Path.Combine(_root, "project");
        await using var session = await ProjectSession.OpenAsync(await SqliteProjectStore.CreateAsync(project, ct), ct);
        var collection = CollectionId.New();
        Assert.IsType<SessionResult.Accepted>(session.Execute("Create", s => s.WithSnapshot(complete.Snapshot).WithCollection(CollectionOperations.Create(collection, "Live", complete.Snapshot))));
        var assets = new AssetStore(project);
        var report = await CoverAcquisition.AcquireAsync(session, collection, new LiveChartCoverSource(http, assets, ImageLimits.Default), assets, RetryBudget.CreateDefault(), null, ct);
        await session.FlushAsync();

        var summary = $"items={complete.UniqueItems} raw={complete.RawOccurrences} completed={report.Completed} cached={report.Cached} missing={report.Missing} failed={report.Failed} paused={report.Paused}";
        TestContext.Current.SendDiagnosticMessage(summary);
        var seasons = complete.Snapshot.Captures.Select(c => $"{c.Season} ({c.Occurrences.Length}): {string.Join(" ", c.Occurrences.Select(o => o.Item.Value))}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), reportName),
            summary + Environment.NewLine + string.Join(Environment.NewLine, seasons) + Environment.NewLine +
            string.Join(Environment.NewLine, report.Entries.Where(e => e.Detail is not null).Select(e => $"{e.Item}: {e.Status} {e.Detail}")));
        Assert.Equal(complete.UniqueItems, report.Total);
        Assert.Equal(report.Total, report.Completed + report.Missing);
        Assert.Equal(SaveState.Saved, session.SaveState);
        Assert.Equal(complete.Snapshot.Sequence.Select(s => s.Item), session.Current.Collection(collection).Entries.Select(e => e.Item));
        Assert.All(session.Current.Collection(collection).Entries.Where(e => e.Cover is not null), e => Assert.Equal(e.AcceptedOccurrence, e.Cover!.Occurrence));
        return complete.Snapshot;
    }
}
