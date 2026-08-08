using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Tests.Models;

public class IpAddressUtilsTests
{
    [Theory]
    [InlineData("224.0.0.251")]      // mDNS
    [InlineData("224.0.0.252")]      // LLMNR
    [InlineData("239.255.255.250")]  // SSDP
    [InlineData("224.0.0.0")]        // lower bound of 224.0.0.0/4
    [InlineData("239.255.255.255")]  // upper bound of 224.0.0.0/4
    [InlineData("255.255.255.255")]  // limited broadcast
    [InlineData("ff02::fb")]         // mDNS over IPv6
    [InlineData("ff02::1")]
    public void IsMulticastOrBroadcast_GroupDestinations_AreTrue(string address)
    {
        IpAddressUtils.IsMulticastOrBroadcast(IPAddress.Parse(address)).Should().BeTrue();
    }

    [Theory]
    [InlineData("223.255.255.255")]  // just below the multicast range
    [InlineData("240.0.0.1")]        // just above it (reserved, but not multicast)
    [InlineData("192.168.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("127.0.0.1")]
    [InlineData("fe80::1")]          // link-local unicast, not multicast
    [InlineData("2001:db8::1")]
    [InlineData("::1")]
    public void IsMulticastOrBroadcast_RealEndpoints_AreFalse(string address)
    {
        IpAddressUtils.IsMulticastOrBroadcast(IPAddress.Parse(address)).Should().BeFalse();
    }

    [Fact]
    public void ScopelessKey_StripsIpv6ScopeSoAdapterAndWireFormsMatch()
    {
        var fromAdapter = IPAddress.Parse("fe80::a414:580a:a0b2:723b%3");
        var fromWire = IPAddress.Parse("fe80::a414:580a:a0b2:723b");

        fromAdapter.ToString().Should().NotBe(fromWire.ToString());   // the trap
        IpAddressUtils.ScopelessKey(fromAdapter)
            .Should().Be(IpAddressUtils.ScopelessKey(fromWire));
    }

    [Fact]
    public void ScopelessKey_Ipv4_IsUnchanged()
    {
        IpAddressUtils.ScopelessKey(IPAddress.Parse("192.168.1.51")).Should().Be("192.168.1.51");
    }
}
