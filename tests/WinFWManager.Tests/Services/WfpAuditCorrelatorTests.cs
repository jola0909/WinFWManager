using System.Net;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class WfpAuditCorrelatorTests
{
    private static readonly DateTime When = new(2026, 8, 10, 9, 8, 14, DateTimeKind.Local);

    private static TrafficEvent Event(int seconds = 0)
        => new()
        {
            Timestamp = When.AddSeconds(seconds),
            Action = TrafficAction.Drop,
            Protocol = TransportProtocol.TCP,
            SourceAddress = IPAddress.Parse("192.168.1.51"),
            SourcePort = 49237,
            DestinationAddress = IPAddress.Parse("1.1.1.1"),
            DestinationPort = 443,
        };

    private static WfpAuditBlock Audit(
        string source = "192.168.1.51", string sourcePort = "49237",
        string dest = "1.1.1.1", string destPort = "443", int seconds = 0)
        => new(When.AddSeconds(seconds), 5157, 72998, 0, "ALE Connect",
            @"\device\harddiskvolume3\windows\system32\curl.exe",
            "Outbound", "6", source, sourcePort, dest, destPort, 5984);

    [Fact]
    public void FindMatch_SameEndpoints_Matches()
    {
        WfpAuditCorrelator.FindMatch(Event(), new[] { Audit() })
            .Should().NotBeNull();
    }

    [Fact]
    public void FindMatch_EndpointsReversed_StillMatches()
    {
        // The audit event labels endpoints from the filter's point of view, which does
        // not always agree with the capture's.
        var reversed = Audit(source: "1.1.1.1", sourcePort: "443",
                             dest: "192.168.1.51", destPort: "49237");

        WfpAuditCorrelator.FindMatch(Event(), new[] { reversed }).Should().NotBeNull();
    }

    [Fact]
    public void FindMatch_DifferentPort_DoesNotMatch()
    {
        WfpAuditCorrelator.FindMatch(Event(), new[] { Audit(sourcePort: "49999") })
            .Should().BeNull();
    }

    [Fact]
    public void FindMatch_DifferentAddress_DoesNotMatch()
    {
        WfpAuditCorrelator.FindMatch(Event(), new[] { Audit(dest: "8.8.8.8") })
            .Should().BeNull();
    }

    [Fact]
    public void FindMatch_WithinSkew_Matches()
    {
        // Audit timestamps have one-second resolution and the sources record at slightly
        // different moments, so a small gap must not prevent a match.
        WfpAuditCorrelator.FindMatch(Event(), new[] { Audit(seconds: 4) }).Should().NotBeNull();
        WfpAuditCorrelator.FindMatch(Event(), new[] { Audit(seconds: -4) }).Should().NotBeNull();
    }

    [Fact]
    public void FindMatch_BeyondSkew_DoesNotMatch()
    {
        // A packet minutes away with the same endpoints is a different connection.
        WfpAuditCorrelator.FindMatch(Event(), new[] { Audit(seconds: 120) }).Should().BeNull();
    }

    [Fact]
    public void FindMatch_NoAudits_ReturnsNull()
    {
        WfpAuditCorrelator.FindMatch(Event(), Array.Empty<WfpAuditBlock>()).Should().BeNull();
    }
}
