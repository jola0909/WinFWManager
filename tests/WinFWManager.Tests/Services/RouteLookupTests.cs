using System.Net;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class RouteLookupTests
{
    [Fact]
    public void IsWildcard_NullOrAnyAddress_IsTrue()
    {
        RouteLookup.IsWildcard(null).Should().BeTrue();
        RouteLookup.IsWildcard(IPAddress.Any).Should().BeTrue();          // 0.0.0.0
        RouteLookup.IsWildcard(IPAddress.IPv6Any).Should().BeTrue();      // ::
    }

    [Theory]
    [InlineData("192.168.1.51")]
    [InlineData("8.8.8.8")]
    [InlineData("127.0.0.1")]
    [InlineData("fe80::1")]
    public void IsWildcard_RealAddress_IsFalse(string address)
    {
        RouteLookup.IsWildcard(IPAddress.Parse(address)).Should().BeFalse();
    }

    [Fact]
    public void BuildSockAddr_Ipv4_HasFamilyAndAddressAtCorrectOffsets()
    {
        var sa = RouteLookup.BuildSockAddr(IPAddress.Parse("203.0.113.5"))!;

        sa.Should().HaveCount(16);          // sizeof(SOCKADDR_IN)
        sa[0].Should().Be(2);               // AF_INET, little-endian USHORT
        sa[1].Should().Be(0);
        sa.Skip(4).Take(4).Should().Equal(203, 0, 113, 5);
    }

    [Fact]
    public void BuildSockAddr_Ipv6_HasFamilyAndAddressAtCorrectOffsets()
    {
        var address = IPAddress.Parse("2001:db8::1");
        var sa = RouteLookup.BuildSockAddr(address)!;

        sa.Should().HaveCount(28);          // sizeof(SOCKADDR_IN6)
        sa[0].Should().Be(23);              // AF_INET6
        sa[1].Should().Be(0);
        sa.Skip(8).Take(16).Should().Equal(address.GetAddressBytes());
    }

    [Fact]
    public void GetBestInterfaceIndex_LoopbackAlwaysRoutable_ReturnsPositiveIndex()
    {
        // Every machine can route to itself, so this must resolve without a network.
        RouteLookup.GetBestInterfaceIndex(IPAddress.Loopback).Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetBestInterfaceIndex_DoesNotThrowForAnyAddressFamily()
    {
        var act = () =>
        {
            RouteLookup.GetBestInterfaceIndex(IPAddress.Parse("8.8.8.8"));
            RouteLookup.GetBestInterfaceIndex(IPAddress.Parse("2001:4860:4860::8888"));
        };

        act.Should().NotThrow();
    }
}
