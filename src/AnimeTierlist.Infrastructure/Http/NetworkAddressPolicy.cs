using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace AnimeTierlist.Infrastructure.Http;

/// <summary>Raised when a host resolves only to local, private, or otherwise non-public addresses.</summary>
public sealed class DisallowedAddressException : IOException
{
    public DisallowedAddressException()
    {
    }

    public DisallowedAddressException(string message)
        : base(message)
    {
    }

    public DisallowedAddressException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Restricts outgoing connections to public unicast addresses, checked after DNS resolution so an allowlisted name
/// that resolves to a local or private network target is still refused.
/// </summary>
public static class NetworkAddressPolicy
{
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                || b[0] == 10
                || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 198 && b[1] is 18 or 19)
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)
                || b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6None.Equals(address)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6Teredo || address.IsIPv6UniqueLocal)
            {
                return false;
            }

            var b = address.GetAddressBytes();
            var documentation = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8;
            var nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0);
            var ipv4Compatible = b[..12].All(x => x == 0);
            if (documentation || ipv4Compatible)
            {
                return false;
            }

            return !nat64 || IsPublic(new IPAddress(b[12..]));
        }

        return false;
    }

    /// <summary><see cref="SocketsHttpHandler.ConnectCallback"/> that connects only to public addresses of the requested host.</summary>
    public static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
        {
            throw new DisallowedAddressException($"{host} does not resolve to a public network address.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
