using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace AnimeTierlist.Desktop.Services;

/// <summary>
/// Manages runtime switching between Dark, Light, System, and Windows High Contrast token dictionaries,
/// keeping every open window's resources and native title bar synchronized.
/// </summary>
public sealed class ThemeManager : IDisposable
{
    private static readonly Uri DarkTokensUri;
    private static readonly Uri LightTokensUri;
    private static readonly Uri HighContrastTokensUri;
    private static readonly Uri TypographyUri;
    private static readonly Uri IconsUri;
    private static readonly Uri ControlsUri;

    static ThemeManager()
    {
        EnsurePackUriRegistered();
        DarkTokensUri = new Uri("/AnimeTierlist;component/Themes/Tokens.Dark.xaml", UriKind.Relative);
        LightTokensUri = new Uri("/AnimeTierlist;component/Themes/Tokens.Light.xaml", UriKind.Relative);
        HighContrastTokensUri = new Uri("/AnimeTierlist;component/Themes/Tokens.HighContrast.xaml", UriKind.Relative);
        TypographyUri = new Uri("/AnimeTierlist;component/Themes/Typography.xaml", UriKind.Relative);
        IconsUri = new Uri("/AnimeTierlist;component/Themes/Icons.xaml", UriKind.Relative);
        ControlsUri = new Uri("/AnimeTierlist;component/Themes/Controls.xaml", UriKind.Relative);
    }

    /// <summary>Ensures WPF's pack:// URI parser is initialized before loading theme dictionaries.</summary>
    public static void EnsurePackUriRegistered()
    {
        if (!UriParser.IsKnownScheme("pack"))
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        }
    }

    /// <summary>
    /// Optional theme override used when <c>Application.Current</c> is not available.
    /// Ensures child views created inside templates inherit the requested theme instead of always Dark.
    /// </summary>
    internal static AppTheme? HeadlessThemeOverride { get; set; }

    /// <summary>Optional Windows High Contrast override used when system parameters are unavailable.</summary>
    internal static bool HeadlessHighContrastOverride { get; set; }

    /// <summary>
    /// Ensures theme resources are available on <paramref name="element"/> when constructed standalone in headless
    /// tests without <c>App.xaml</c>. Does nothing when <c>Application.Current</c> already provides the theme.
    /// </summary>
    public static void EnsureViewResources(FrameworkElement element, AppTheme fallbackTheme = AppTheme.Dark)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (System.Windows.Application.Current is { Resources.MergedDictionaries.Count: > 0 })
        {
            return;
        }

        if (element.Resources.MergedDictionaries.Any(d => IsTokenDictionary(d.Source)))
        {
            return;
        }

        ApplyToDictionary(element.Resources, HeadlessThemeOverride ?? fallbackTheme, HeadlessHighContrastOverride);
    }

    /// <summary>Loads the four theme dictionaries (tokens, typography, icons, controls) into any <see cref="ResourceDictionary"/>.</summary>
    public static void ApplyToDictionary(ResourceDictionary target, AppTheme theme, bool highContrast = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        var tokenUri = highContrast
            ? HighContrastTokensUri
            : theme == AppTheme.Light ? LightTokensUri : DarkTokensUri;
        target.MergedDictionaries.Clear();
        target.MergedDictionaries.Add(new ResourceDictionary { Source = tokenUri });
        target.MergedDictionaries.Add(new ResourceDictionary { Source = TypographyUri });
        target.MergedDictionaries.Add(new ResourceDictionary { Source = IconsUri });
        target.MergedDictionaries.Add(new ResourceDictionary { Source = ControlsUri });
    }

    private AppTheme _preference = AppTheme.Dark;
    private bool _disposed;

    /// <summary>Starts listening for Windows High Contrast and system theme changes.</summary>
    public ThemeManager()
    {
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>True when the effective resolved theme is dark (excluding Windows High Contrast).</summary>
    public bool IsEffectiveDark { get; private set; } = true;

    /// <summary>Applies the requested theme preference across the application and all open windows.</summary>
    public void Apply(AppTheme theme)
    {
        _preference = theme;
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        Uri targetUri;
        bool useDarkTitleBar;
        if (SystemParameters.HighContrast)
        {
            targetUri = HighContrastTokensUri;
            useDarkTitleBar = false;
            IsEffectiveDark = false;
        }
        else
        {
            var effectiveDark = theme switch
            {
                AppTheme.Dark => true,
                AppTheme.Light => false,
                _ => IsWindowsDarkMode(),
            };
            targetUri = effectiveDark ? DarkTokensUri : LightTokensUri;
            useDarkTitleBar = effectiveDark;
            IsEffectiveDark = effectiveDark;
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var next = new ResourceDictionary { Source = targetUri };
        if (dictionaries.Count > 0 && IsTokenDictionary(dictionaries[0].Source))
        {
            dictionaries[0] = next;
        }
        else
        {
            dictionaries.Insert(0, next);
        }

        foreach (Window window in app.Windows)
        {
            WindowChromeHelper.ApplyTitleBarTheme(window, useDarkTitleBar);
        }
    }

    /// <summary>Applies the current title bar theme to a newly created window on <c>SourceInitialized</c>.</summary>
    public void AttachWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) => WindowChromeHelper.ApplyTitleBarTheme(window, IsEffectiveDark);
        if (window.IsLoaded)
        {
            WindowChromeHelper.ApplyTitleBarTheme(window, IsEffectiveDark);
        }
    }

    /// <summary>Stops listening to system theme notifications.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    /// <summary>Reads Windows' read-only AppsUseLightTheme preference without ever writing to the registry.</summary>
    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value ? value == 0 : true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            return true;
        }
    }

    private static bool IsTokenDictionary(Uri? source) =>
        source is not null && source.OriginalString.Contains("Themes/Tokens.", StringComparison.OrdinalIgnoreCase);

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            ReapplyOnDispatcher();
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
        {
            ReapplyOnDispatcher();
        }
    }

    private void ReapplyOnDispatcher()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        _ = app.Dispatcher.BeginInvoke(() => Apply(_preference));
    }
}
