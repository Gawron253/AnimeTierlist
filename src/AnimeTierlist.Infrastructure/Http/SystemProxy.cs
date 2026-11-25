using System.ComponentModel;
using System.Net.Http;

namespace AnimeTierlist.Infrastructure.Http;

/// <summary>
/// Reads, but never uses, the Windows or environment proxy configuration. The app always connects directly;
/// this only lets an "unreachable" message say that the system routes the source through a proxy.
/// </summary>
public static class SystemProxy
{
    /// <summary>
    /// True when the system proxy settings send <paramref name="destination"/> through a proxy. May consult proxy
    /// auto-configuration, which can block, so call it off the UI thread. Unreadable settings count as no proxy.
    /// </summary>
    public static bool RoutesThroughProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        try
        {
            var proxy = HttpClient.DefaultProxy;
            if (proxy.IsBypassed(destination))
            {
                return false;
            }

            var route = proxy.GetProxy(destination);
            return route is not null && route != destination;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or Win32Exception or UriFormatException)
        {
            return false;
        }
    }
}
