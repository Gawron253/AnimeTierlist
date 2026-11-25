using AnimeTierlist.Core;

namespace AnimeTierlist.Core.Tests;

/// <summary>Domain rules for OP/ED video classification, priority ordering, detail merging, and external URL validation.</summary>
public sealed class AnimeDetailsAndVideosTests
{
    [Theory]
    [InlineData("OP", AnimeVideoKind.Opening)]
    [InlineData("OP 2", AnimeVideoKind.Opening)]
    [InlineData("OP2 (Creditless)", AnimeVideoKind.Opening)]
    [InlineData("Opening Theme Music Video", AnimeVideoKind.Opening)]
    [InlineData("ED", AnimeVideoKind.Ending)]
    [InlineData("ED 2", AnimeVideoKind.Ending)]
    [InlineData("ED 2 (Pit ver.)", AnimeVideoKind.Ending)]
    [InlineData("Ending Theme Song", AnimeVideoKind.Ending)]
    [InlineData("PV 1", AnimeVideoKind.Other)]
    [InlineData("Climax PV", AnimeVideoKind.Other)]
    [InlineData("Super Teaser", AnimeVideoKind.Other)]
    [InlineData("Announcement CM", AnimeVideoKind.Other)]
    [InlineData("Episode 6 Insert Song Lyric Video", AnimeVideoKind.Other)]
    public void Classifier_identifies_openings_endings_and_other_videos(string title, AnimeVideoKind expected) =>
        Assert.Equal(expected, AnimeVideoClassifier.Classify(title));

    [Fact]
    public void Theme_songs_order_openings_before_endings_in_natural_sequence()
    {
        var videos = new[]
        {
            new AnimeVideo("Climax PV", new Uri("https://www.youtube.com/watch?v=pv1"), AnimeVideoKind.Other),
            new AnimeVideo("ED 2 (Pit ver.)", new Uri("https://www.youtube.com/watch?v=ed2p"), AnimeVideoKind.Ending),
            new AnimeVideo("ED 2", new Uri("https://www.youtube.com/watch?v=ed2"), AnimeVideoKind.Ending),
            new AnimeVideo("OP 2", new Uri("https://www.youtube.com/watch?v=op2"), AnimeVideoKind.Opening),
            new AnimeVideo("Ending Theme Song", new Uri("https://www.youtube.com/watch?v=edmv"), AnimeVideoKind.Ending),
            new AnimeVideo("ED", new Uri("https://www.youtube.com/watch?v=ed1"), AnimeVideoKind.Ending),
            new AnimeVideo("Opening Theme Music Video", new Uri("https://www.youtube.com/watch?v=opmv"), AnimeVideoKind.Opening),
            new AnimeVideo("OP", new Uri("https://www.youtube.com/watch?v=op1"), AnimeVideoKind.Opening),
        };

        var ordered = AnimeVideoClassifier.OrderThemeSongs(videos);

        Assert.Equal(
            ["OP", "OP 2", "Opening Theme Music Video", "ED", "ED 2", "ED 2 (Pit ver.)", "Ending Theme Song"],
            ordered.Select(v => v.Title));
    }

    [Fact]
    public void External_url_policy_accepts_allowed_https_links_and_rejects_untrusted_ones()
    {
        Assert.True(ExternalUrlPolicy.IsAllowedAniListUrl(new Uri("https://anilist.co/anime/185660")));
        Assert.True(ExternalUrlPolicy.IsAllowedMyAnimeListUrl(new Uri("https://myanimelist.net/anime/60543")));
        Assert.True(ExternalUrlPolicy.IsAllowedVideoUrl(new Uri("https://www.youtube.com/watch?v=DCCRNzKvWRg")));
        Assert.True(ExternalUrlPolicy.IsAllowedVideoUrl(new Uri("https://youtu.be/DCCRNzKvWRg")));
        Assert.True(ExternalUrlPolicy.IsAllowedVideoUrl(new Uri("https://x.com/animeDANDADANen/status/1933177477272412307")));

        Assert.False(ExternalUrlPolicy.IsAllowedAniListUrl(new Uri("http://anilist.co/anime/185660")));
        Assert.False(ExternalUrlPolicy.IsAllowedAniListUrl(new Uri("https://anilist.co/user/foo")));
        Assert.False(ExternalUrlPolicy.IsAllowedMyAnimeListUrl(new Uri("https://evil.example/anime/60543")));
        Assert.False(ExternalUrlPolicy.IsAllowedVideoUrl(new Uri("https://evil.example/watch?v=123")));
        Assert.False(ExternalUrlPolicy.IsAllowedBrowserLaunchUrl(new Uri("file:///C:/Windows/System32/cmd.exe")));

        Assert.Throws<ArgumentException>(() => new SourceDetails(aniListUrl: new Uri("https://evil.example/anime/1")));
        Assert.Throws<ArgumentException>(() => new SourceDetails(myAnimeListUrl: new Uri("http://myanimelist.net/anime/1")));
        Assert.Throws<ArgumentException>(() => new AnimeVideo("OP", new Uri("https://evil.example/op"), AnimeVideoKind.Opening));
    }

    [Fact]
    public void Source_details_merge_fills_missing_fields_without_overwriting_existing_values()
    {
        var primary = new SourceDetails(
            rating: "8.5/10",
            metadata: "TV · Manga · 12 eps × 24m",
            aniListUrl: new Uri("https://anilist.co/anime/185660"));
        var fallback = new SourceDetails(
            rating: "8.49 out of 10",
            premiere: "Jul 3, 2025",
            synopsis: "Fallback synopsis.",
            studios: ["Science SARU"],
            tags: ["Action", "Comedy"],
            myAnimeListUrl: new Uri("https://myanimelist.net/anime/60543"));

        var merged = primary.MergeWith(fallback);

        Assert.Equal("8.5/10", merged.Rating);
        Assert.Equal("Jul 3, 2025", merged.Premiere);
        Assert.Equal("TV · Manga · 12 eps × 24m", merged.Metadata);
        Assert.Equal("Fallback synopsis.", merged.Synopsis);
        Assert.Equal(["Science SARU"], merged.Studios);
        Assert.Equal(["Action", "Comedy"], merged.Tags);
        Assert.Equal(new Uri("https://anilist.co/anime/185660"), merged.AniListUrl);
        Assert.Equal(new Uri("https://myanimelist.net/anime/60543"), merged.MyAnimeListUrl);
    }
}
