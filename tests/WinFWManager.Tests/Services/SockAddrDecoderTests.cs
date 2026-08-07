using System.Net;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class SockAddrDecoderTests
{
    [Fact]
    public void Decode_Ipv4Sockaddr_ReturnsAddressAndPort()
    {
        // AF_INET (2), port 9099 (0x238B), 172.24.0.1
        var b = new byte[] { 2, 0, 0x23, 0x8B, 172, 24, 0, 1 };
        var (ip, port) = SockAddrDecoder.Decode(b);
        ip.Should().Be(IPAddress.Parse("172.24.0.1"));
        port.Should().Be(9099);
    }

    [Fact]
    public void Decode_Ipv6Sockaddr_ReturnsAddressAndPort()
    {
        var b = new byte[28];
        b[0] = 23; // AF_INET6
        b[2] = 0x01; b[3] = 0xBB; // port 443
        b[8] = 0xfe; b[9] = 0x80; b[23] = 0x01; // fe80::1
        var (ip, port) = SockAddrDecoder.Decode(b);
        ip.Should().Be(IPAddress.Parse("fe80::1"));
        port.Should().Be(443);
    }

    [Fact]
    public void Decode_DualStackMapped_NormalizesToIpv4()
    {
        var b = new byte[28];
        b[0] = 23;
        b[2] = 0x01; b[3] = 0xBB;
        // ::ffff:10.0.0.42
        b[18] = 0xFF; b[19] = 0xFF; b[20] = 10; b[21] = 0; b[22] = 0; b[23] = 42;
        var (ip, port) = SockAddrDecoder.Decode(b);
        ip.Should().Be(IPAddress.Parse("10.0.0.42"));
    }

    [Fact]
    public void Decode_BareIpv4Bytes_ReturnsAddressNoPort()
    {
        var (ip, port) = SockAddrDecoder.Decode(new byte[] { 172, 24, 15, 184 });
        ip.Should().Be(IPAddress.Parse("172.24.15.184"));
        port.Should().Be(0);
    }

    [Fact]
    public void Decode_TooShort_ReturnsNull()
    {
        SockAddrDecoder.Decode(new byte[] { 2, 0 }).Ip.Should().BeNull();
    }
}
