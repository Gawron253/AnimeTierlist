using System.Net;
using AnimeTierlist.Infrastructure.Http;

namespace AnimeTierlist.Infrastructure.Tests.Http;

public sealed class NetworkAddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("192.0.2.10")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("64:ff9b::a00:1")]
    public void Local_private_and_special_addresses_are_not_public(string address) =>
        Assert.False(NetworkAddressPolicy.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("104.21.32.1")]
    [InlineData("172.32.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700::1")]
    [InlineData("::ffff:104.21.32.1")]
    public void Public_unicast_addresses_are_allowed(string address) =>
        Assert.True(NetworkAddressPolicy.IsPublic(IPAddress.Parse(address)));
}
