using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class DropReasonMapperTests
{
    [Fact]
    public void NetworkReason_256_IsWfpFirewall()
        => DropReasonMapper.Network(256).Should().Be(DropReasonMapper.FirewallLabel);

    [Theory]
    [InlineData(3, "Port unreachable")]
    [InlineData(6, "No route")]
    [InlineData(8, "Inspection drop (WFP)")]
    [InlineData(10, "Administratively prohibited")]
    [InlineData(16, "Hop limit exceeded (TTL)")]
    public void NetworkReason_DocumentedCodes_MapToText(int code, string expected)
        => DropReasonMapper.Network(code).Should().Be(expected);

    [Theory]
    [InlineData(4, "Endpoint not found (no listener)")]
    [InlineData(7, "Receive inspection failure (WFP)")]
    [InlineData(10, "Invalid RST segment")]
    [InlineData(17, "Duplicate segment")]
    public void TransportReason_DocumentedCodes_MapToText(int code, string expected)
        => DropReasonMapper.Transport(code).Should().Be(expected);

    [Fact]
    public void UnknownNetworkReason_FallsBackWithLayerTag()
        => DropReasonMapper.Network(9999).Should().Be("Network drop (reason 9999)");

    [Fact]
    public void UnknownTransportReason_FallsBackWithLayerTag()
        => DropReasonMapper.Transport(9999).Should().Be("Transport drop (reason 9999)");

    [Theory]
    [InlineData("Firewall (WFP filter)")]
    [InlineData("Administratively prohibited")]
    [InlineData("Inspection drop (WFP)")]
    [InlineData("Inspection absorb (WFP)")]
    [InlineData("Receive inspection failure (WFP)")]
    public void IsPolicyDrop_FilteringPlatformDecisions_AreTrue(string reason)
    {
        DropReasonMapper.IsPolicyDrop(reason).Should().BeTrue();
    }

    [Theory]
    [InlineData("Duplicate segment")]
    [InlineData("Bad checksum")]
    [InlineData("No route")]
    [InlineData("Endpoint not found (no listener)")]
    [InlineData("Connection closing (FIN_WAIT_2)")]
    [InlineData(null)]
    public void IsPolicyDrop_StackDiscards_AreFalse(string? reason)
    {
        // A duplicate segment or bad checksum has no rule behind it, so offering to
        // find one would be misleading.
        DropReasonMapper.IsPolicyDrop(reason).Should().BeFalse();
    }
}
