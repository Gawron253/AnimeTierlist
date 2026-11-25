using System.Collections.Immutable;
using AnimeTierlist.Core;

namespace AnimeTierlist.Core.Tests;

/// <summary>Small hand-written source data. Letters are readable aliases for LiveChart IDs.</summary>
internal static class TestData
{
    public static readonly LiveChartId A = new(1);
    public static readonly LiveChartId B = new(2);
    public static readonly LiveChartId C = new(3);
    public static readonly LiveChartId D = new(4);
    public static readonly LiveChartId X = new(24);

    private static readonly DateTimeOffset Retrieved = new(2026, 11, 17, 22, 0, 0, TimeSpan.Zero);

    public static string Name(LiveChartId id) => id.Value switch
    {
        1 => "A",
        2 => "B",
        3 => "C",
        4 => "D",
        24 => "X",
        _ => id.ToString(),
    };

    public static SourceOccurrence Occ(int ordinal, LiveChartId id, string? displayed = null, string? english = null) =>
        new(ordinal, id, new SourceTitles(displayed ?? $"Title {Name(id)}", english, $"Romaji {Name(id)}", $"Native {Name(id)}"),
            [new ImageCandidate("large", new Uri($"https://u.livechart.me/anime/{id}/poster_image/x.webp/large.jpg"))]);

    public static SeasonCapture Capture(int year, Season season, params LiveChartId[] ids) =>
        CaptureWithTitles(year, season, ids.Select(id => (id, (string?)null)).ToArray());

    public static SeasonCapture CaptureWithTitles(int year, Season season, params (LiveChartId Id, string? Displayed)[] cards) =>
        new(new SeasonKey(year, season), CatalogueCategory.Tv, new Uri($"https://www.livechart.me/{season.ToString().ToLowerInvariant()}-{year}/tv"), Retrieved, "test-1",
            cards.Select((c, i) => Occ(i, c.Id, c.Displayed)).ToImmutableArray());

    public static SourceSnapshot SeasonSnapshot(params LiveChartId[] ids) =>
        new(SnapshotId.New(), new AcquisitionContext(2025, Season.Summer, CatalogueCategory.Tv), [Capture(2025, Season.Summer, ids)]);

    public static SourceSnapshot SeasonSnapshotWithTitles(params (LiveChartId Id, string? Displayed)[] cards) =>
        new(SnapshotId.New(), new AcquisitionContext(2025, Season.Summer, CatalogueCategory.Tv), [CaptureWithTitles(2025, Season.Summer, cards)]);

    public static IEnumerable<string> Names(IEnumerable<LiveChartId> ids) => ids.Select(Name);

    public static IEnumerable<string> Names(IEnumerable<BoardEntryKey> keys) =>
        keys.Select(k => k.IsDuplicate ? $"{Name(k.Item)}#{k.CopyNumber}" : Name(k.Item));

    public static IEnumerable<string> Names(CollectionState c) => Names(c.Entries.Select(e => e.Item));

    public static AssetHash Hash(char c) => new(new string(c, 64));

    public static CoverProvenance Cover(OccurrenceRef occurrence, char hash) =>
        new(occurrence, new Uri("https://u.livechart.me/x/large.jpg"), new Uri("https://u.livechart.me/x/large.jpg"), "Accept: image/jpeg,image/png", Hash(hash));
}
