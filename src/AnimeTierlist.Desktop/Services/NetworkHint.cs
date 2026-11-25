using AnimeTierlist.Infrastructure.Http;
using AnimeTierlist.Infrastructure.LiveChart;

namespace AnimeTierlist.Desktop.Services;

/// <summary>Explains an unreachable source. LiveChart requests always connect directly and never through a proxy.</summary>
public static class NetworkHint
{
    public const string Unreachable =
        "LiveChart could not be reached. Check the internet connection and try again. " +
        "This version connects to LiveChart directly and does not use proxy servers, so networks that allow web access only through a proxy are not supported.";

    public const string ProxyConfigured =
        " Windows is set to send LiveChart traffic through a proxy, which may be why the direct connection failed.";

    /// <summary>The unreachable explanation, noting when the system proxy settings route LiveChart through a proxy.</summary>
    public static async Task<string> DescribeUnreachableAsync()
    {
        // Proxy auto-configuration can block, so read the settings off the UI thread.
        var proxied = await Task.Run(() => SystemProxy.RoutesThroughProxy(LiveChartRoutes.Origin)).ConfigureAwait(true);
        return proxied ? Unreachable + ProxyConfigured : Unreachable;
    }
}
