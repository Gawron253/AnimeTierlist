using System.IO;
using Microsoft.Web.WebView2.Core;

namespace AnimeTierlist.Desktop.Services;

/// <summary>
/// The single WebView2 environment behind the in-app YouTube player. It is created on first use, so app
/// startup never loads the browser engine. Its isolated profile lives in <c>webview2/</c> under the data root: never
/// inside a project or archive, never a personal browser profile. The profile is kept between sessions so YouTube
/// remembers the cookie-consent choice instead of asking every time.
/// </summary>
public sealed class VideoPlayerEnvironment
{
    public const string ProfileFolderName = "webview2";

    private Task<CoreWebView2Environment>? _environment;

    public VideoPlayerEnvironment(string dataRoot, string applicationDirectory)
    {
        UserDataFolder = Path.Combine(dataRoot, ProfileFolderName);
        PlayerAssetsFolder = Path.Combine(applicationDirectory, "Assets", "Player");
    }

    /// <summary>The WebView2 user data folder (cookies, cache) for the player only.</summary>
    public string UserDataFolder { get; }

    /// <summary>The packaged player page folder mapped to the virtual host.</summary>
    public string PlayerAssetsFolder { get; }

    /// <summary>True when a WebView2 Runtime is installed. The app never installs or prompts for one.</summary>
    public static bool IsRuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the shared environment, creating it on first call. A failed attempt is not cached, so the next player
    /// tries again. Call only on the UI thread.
    /// </summary>
    public Task<CoreWebView2Environment> GetAsync()
    {
        if (_environment is null or { IsFaulted: true } or { IsCanceled: true })
        {
            _environment = CreateAsync();
        }

        return _environment;
    }

    private async Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(UserDataFolder);
        var options = new CoreWebView2EnvironmentOptions
        {
            // The player only opens when the user chooses to play a video, so that click is the user's intent
            // to start playback. Without this, Chromium blocks autoplay with sound inside the new window.
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            AllowSingleSignOnUsingOSPrimaryAccount = false,
            AreBrowserExtensionsEnabled = false,
        };
        return await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, UserDataFolder, options);
    }
}
