using AnimeTierlist.Core;

namespace AnimeTierlist.Core.Tests;

/// <summary>Rules that decide what the in-app YouTube player may load and how it reads player status.</summary>
public sealed class YouTubeTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=0gU8da5iHAA", "0gU8da5iHAA")]
    [InlineData("https://youtube.com/watch?v=0gU8da5iHAA&t=42s", "0gU8da5iHAA")]
    [InlineData("https://m.youtube.com/watch?feature=share&v=hvcWx-8RfDQ", "hvcWx-8RfDQ")]
    [InlineData("https://WWW.YOUTUBE.COM/watch?v=1__lAvsdai8", "1__lAvsdai8")]
    [InlineData("https://youtu.be/BJXBr_aFF8s", "BJXBr_aFF8s")]
    [InlineData("https://youtu.be/BJXBr_aFF8s?si=abc", "BJXBr_aFF8s")]
    [InlineData("https://www.youtube.com/embed/ZwnbCz6atKE", "ZwnbCz6atKE")]
    [InlineData("https://www.youtube.com/shorts/ZwnbCz6atKE", "ZwnbCz6atKE")]
    [InlineData("https://www.youtube.com/live/ZwnbCz6atKE", "ZwnbCz6atKE")]
    public void Supported_youtube_links_yield_the_case_sensitive_video_id(string url, string expected)
    {
        Assert.True(YouTubeVideoId.TryParse(new Uri(url), out var id));
        Assert.Equal(expected, id.Value);
    }

    [Theory]
    [InlineData("http://www.youtube.com/watch?v=0gU8da5iHAA")] // not HTTPS
    [InlineData("https://www.youtube.com:8443/watch?v=0gU8da5iHAA")] // non-default port
    [InlineData("https://user:pass@www.youtube.com/watch?v=0gU8da5iHAA")] // credentials
    [InlineData("https://www.youtube.com.evil.example/watch?v=0gU8da5iHAA")] // lookalike host
    [InlineData("https://evilyoutube.com/watch?v=0gU8da5iHAA")]
    [InlineData("https://www.youtube-nocookie.com/embed/0gU8da5iHAA")] // embed host is built, never accepted as input
    [InlineData("https://x.com/someone/status/123")] // allowed video host, but not playable in the app
    [InlineData("https://www.livechart.me/anime/12979/videos")]
    [InlineData("https://www.youtube.com/watch?v=0gU8da5iHA")] // 10 characters
    [InlineData("https://www.youtube.com/watch?v=0gU8da5iHAAx")] // 12 characters
    [InlineData("https://www.youtube.com/watch?v=0gU8da5i%3CA")] // escaped character outside the alphabet
    [InlineData("https://www.youtube.com/watch?v=0gU8da5iHAA&v=hvcWx-8RfDQ")] // ambiguous duplicate parameter
    [InlineData("https://www.youtube.com/watch")]
    [InlineData("https://www.youtube.com/playlist?list=PL0123456789")]
    [InlineData("https://www.youtube.com/embed/0gU8da5iHAA/extra")]
    [InlineData("https://youtu.be/")]
    [InlineData("https://youtu.be/BJXBr_aFF8s/extra")]
    public void Other_links_are_not_playable_in_the_app(string url) =>
        Assert.False(YouTubeVideoId.TryParse(new Uri(url), out _));

    [Fact]
    public void Missing_or_relative_links_are_not_playable()
    {
        Assert.False(YouTubeVideoId.TryParse(null, out _));
        Assert.False(YouTubeVideoId.TryParse(new Uri("/watch?v=0gU8da5iHAA", UriKind.Relative), out _));
    }

    [Fact]
    public void Player_page_url_carries_only_the_validated_id_on_the_virtual_host()
    {
        Assert.True(YouTubeVideoId.TryParse(new Uri("https://youtu.be/BJXBr_aFF8s"), out var id));
        var page = YouTubePlayerPolicy.PlayerPageUrl(id);

        Assert.Equal("https://player.animetierlist.invalid/player.html?v=BJXBr_aFF8s", page.AbsoluteUri);
        Assert.True(YouTubePlayerPolicy.IsPlayerPage(page));
    }

    [Theory]
    [InlineData("https://player.animetierlist.invalid/player.html?v=BJXBr_aFF8s", true)]
    [InlineData("https://PLAYER.animetierlist.invalid/player.html", true)]
    [InlineData("http://player.animetierlist.invalid/player.html", false)]
    [InlineData("https://player.animetierlist.invalid:444/player.html", false)]
    [InlineData("https://player.animetierlist.invalid/other.html", false)]
    [InlineData("https://player.animetierlist.invalid/Player.html", false)]
    [InlineData("https://player.animetierlist.invalid.evil.example/player.html", false)]
    [InlineData("https://www.youtube.com/watch?v=BJXBr_aFF8s", false)]
    [InlineData("https://www.youtube-nocookie.com/embed/BJXBr_aFF8s", false)]
    [InlineData("about:blank", false)]
    public void Only_the_packaged_player_page_may_load_at_the_top_level(string url, bool allowed) =>
        Assert.Equal(allowed, YouTubePlayerPolicy.IsPlayerPage(new Uri(url)));

    [Fact]
    public void Player_messages_are_parsed_strictly()
    {
        Assert.IsType<YouTubePlayerMessage.Ready>(YouTubePlayerPolicy.ParseMessage("ready"));
        Assert.IsType<YouTubePlayerMessage.Started>(YouTubePlayerPolicy.ParseMessage("started"));
        Assert.IsType<YouTubePlayerMessage.Unreachable>(YouTubePlayerPolicy.ParseMessage("unreachable"));
        Assert.Equal(new YouTubePlayerMessage.Failed(150), YouTubePlayerPolicy.ParseMessage("error:150"));
        Assert.Equal(new YouTubePlayerMessage.Failed(2), YouTubePlayerPolicy.ParseMessage("error:2"));

        foreach (var junk in new[] { null, "", "Ready", " ready", "ready\n", "error:", "error:-1", "error:15a", "error:12345", "error: 5", "error:１", "https://example.com" })
        {
            Assert.Null(YouTubePlayerPolicy.ParseMessage(junk));
        }
    }

    [Theory]
    [InlineData(101, YouTubePlayerFailure.EmbeddingDisabled)]
    [InlineData(150, YouTubePlayerFailure.EmbeddingDisabled)]
    [InlineData(100, YouTubePlayerFailure.NotFound)]
    [InlineData(153, YouTubePlayerFailure.IdentificationRejected)]
    [InlineData(2, YouTubePlayerFailure.PlaybackError)]
    [InlineData(5, YouTubePlayerFailure.PlaybackError)]
    [InlineData(152, YouTubePlayerFailure.PlaybackError)]
    public void Player_error_codes_map_to_fallback_reasons(int code, YouTubePlayerFailure expected) =>
        Assert.Equal(expected, YouTubePlayerPolicy.ClassifyError(code));
}
