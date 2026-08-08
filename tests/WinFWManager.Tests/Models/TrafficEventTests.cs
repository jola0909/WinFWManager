using WinFWManager.Core.Models;
using System.Net;

namespace WinFWManager.Tests.Models;

public class TrafficEventTests
{
    [Fact]
    public void TrafficEvent_ShouldStoreAllProperties()
    {
        var evt = new TrafficEvent
        {
            Timestamp = new DateTime(2026, 2, 27, 10, 0, 0),
            Direction = TrafficDirection.Inbound,
            Protocol = TransportProtocol.TCP,
            SourceAddress = IPAddress.Parse("192.168.1.100"),
            SourcePort = 54321,
            DestinationAddress = IPAddress.Parse("10.0.0.1"),
            DestinationPort = 443,
            Action = TrafficAction.Allow,
            ProcessId = 1234,
            ProcessName = "chrome.exe",
            InterfaceName = "Ethernet",
            Profile = FirewallProfile.Private,
            Country = "US",
            Hostname = "example.com"
        };

        evt.Timestamp.Should().Be(new DateTime(2026, 2, 27, 10, 0, 0));
        evt.Direction.Should().Be(TrafficDirection.Inbound);
        evt.Protocol.Should().Be(TransportProtocol.TCP);
        evt.SourceAddress.Should().Be(IPAddress.Parse("192.168.1.100"));
        evt.SourcePort.Should().Be(54321);
        evt.DestinationAddress.Should().Be(IPAddress.Parse("10.0.0.1"));
        evt.DestinationPort.Should().Be(443);
        evt.Action.Should().Be(TrafficAction.Allow);
        evt.ProcessId.Should().Be(1234);
        evt.ProcessName.Should().Be("chrome.exe");
        evt.InterfaceName.Should().Be("Ethernet");
        evt.Profile.Should().Be(FirewallProfile.Private);
        evt.Country.Should().Be("US");
        evt.Hostname.Should().Be("example.com");
    }

    [Fact]
    public void IsWslTraffic_WhenInterfaceContainsWSL_ReturnsTrue()
    {
        var evt = new TrafficEvent { InterfaceName = "vEthernet (WSL)" };
        evt.IsWslTraffic.Should().BeTrue();
    }

    [Fact]
    public void IsWslTraffic_WhenPhysicalAdapter_ReturnsFalse()
    {
        var evt = new TrafficEvent { InterfaceName = "Ethernet" };
        evt.IsWslTraffic.Should().BeFalse();
    }

    [Fact]
    public void IsHyperVTraffic_WhenInterfaceContainsHyperV_ReturnsTrue()
    {
        var evt = new TrafficEvent { InterfaceName = "vEthernet (Default Switch)" };
        evt.IsHyperVTraffic.Should().BeTrue();
    }

    [Fact]
    public void IsWslTraffic_WhenAdapterTypeWsl_ReturnsTrue()
    {
        var evt = new TrafficEvent { AdapterType = AdapterType.WSL };
        evt.IsWslTraffic.Should().BeTrue();
    }

    [Fact]
    public void IsHyperVTraffic_WhenAdapterTypeVSwitch_ReturnsTrue()
    {
        var evt = new TrafficEvent { AdapterType = AdapterType.VSwitch };
        evt.IsHyperVTraffic.Should().BeTrue();
        evt.IsWslTraffic.Should().BeFalse();
    }

    [Fact]
    public void IsHyperVTraffic_WslAdapterIsNotClassifiedHyperV()
    {
        var evt = new TrafficEvent { AdapterType = AdapterType.WSL, InterfaceName = "vEthernet (WSL)" };
        evt.IsWslTraffic.Should().BeTrue();
        evt.IsHyperVTraffic.Should().BeFalse();
    }

    [Fact]
    public void IsPrivateAddress_WhenRfc1918_ReturnsTrue()
    {
        var evt = new TrafficEvent { DestinationAddress = IPAddress.Parse("192.168.1.1") };
        evt.IsDestinationPrivate.Should().BeTrue();
    }

    [Fact]
    public void FlowDescription_DroppedInboundWsl_ShowsGuestArrowNicBlocked()
    {
        var evt = new TrafficEvent
        {
            Direction = TrafficDirection.Inbound,
            Action = TrafficAction.Drop,
            AdapterType = AdapterType.WSL,
            InterfaceName = "vEthernet (WSL)"
        };
        evt.FlowDescription.Should().Be("WSL guest → vEthernet (WSL) ⛔");
    }

    [Fact]
    public void FlowDescription_AllowedOutboundPublic_ShowsNicArrowInternet()
    {
        var evt = new TrafficEvent
        {
            Direction = TrafficDirection.Outbound,
            Action = TrafficAction.Allow,
            InterfaceName = "Ethernet",
            DestinationAddress = System.Net.IPAddress.Parse("8.8.8.8")
        };
        evt.FlowDescription.Should().Be("Ethernet → internet ✓");
    }

    [Fact]
    public void FlowDescription_AllowedOutboundPrivate_ShowsNicArrowLan()
    {
        var evt = new TrafficEvent
        {
            Direction = TrafficDirection.Outbound,
            Action = TrafficAction.Allow,
            InterfaceName = "Ethernet",
            DestinationAddress = System.Net.IPAddress.Parse("10.0.0.5")
        };
        evt.FlowDescription.Should().Be("Ethernet → LAN ✓");
    }

    [Fact]
    public void FlowDescription_InboundNonWsl_ShowsRemoteArrowNic()
    {
        var evt = new TrafficEvent
        {
            Direction = TrafficDirection.Inbound,
            Action = TrafficAction.Allow,
            InterfaceName = "Ethernet",
            SourceAddress = System.Net.IPAddress.Parse("192.168.1.50")
        };
        evt.FlowDescription.Should().Be("LAN → Ethernet ✓");
    }

    [Fact]
    public void RemoteAddress_Outbound_IsDestination_Inbound_IsSource()
    {
        var outbound = new TrafficEvent
        {
            Direction = TrafficDirection.Outbound,
            SourceAddress = IPAddress.Parse("192.168.1.51"),
            DestinationAddress = IPAddress.Parse("8.8.8.8"),
        };
        outbound.RemoteAddress.Should().Be(IPAddress.Parse("8.8.8.8"));
        outbound.LocalAddress.Should().Be(IPAddress.Parse("192.168.1.51"));

        var inbound = new TrafficEvent
        {
            Direction = TrafficDirection.Inbound,
            SourceAddress = IPAddress.Parse("8.8.8.8"),
            DestinationAddress = IPAddress.Parse("192.168.1.51"),
        };
        // The regression that made this machine its own top talker: grouping inbound
        // events by destination buckets everything under the local address.
        inbound.RemoteAddress.Should().Be(IPAddress.Parse("8.8.8.8"));
        inbound.LocalAddress.Should().Be(IPAddress.Parse("192.168.1.51"));
    }

    [Fact]
    public void RemotePort_Outbound_IsDestination_Inbound_IsSource()
    {
        var outbound = new TrafficEvent
        {
            Direction = TrafficDirection.Outbound, SourcePort = 58451, DestinationPort = 443,
        };
        outbound.RemotePort.Should().Be(443);
        outbound.LocalPort.Should().Be(58451);

        var inbound = new TrafficEvent
        {
            Direction = TrafficDirection.Inbound, SourcePort = 443, DestinationPort = 58451,
        };
        inbound.RemotePort.Should().Be(443);
        inbound.LocalPort.Should().Be(58451);
    }

    [Fact]
    public void FlowKey_BothDirectionsOfOneConversation_Match()
    {
        // Packets travelling each way must collapse to one flow, otherwise a single
        // stream counts as two conversations.
        var sent = new TrafficEvent
        {
            Direction = TrafficDirection.Outbound, Protocol = TransportProtocol.UDP,
            SourcePort = 58451, DestinationPort = 443,
        };
        var received = new TrafficEvent
        {
            Direction = TrafficDirection.Inbound, Protocol = TransportProtocol.UDP,
            SourcePort = 443, DestinationPort = 58451,
        };

        sent.FlowKey.Should().Be(received.FlowKey);
    }

    [Fact]
    public void FlowKey_DifferentLocalPortOrProtocol_AreDifferentFlows()
    {
        var a = new TrafficEvent
        {
            Direction = TrafficDirection.Outbound, Protocol = TransportProtocol.TCP,
            SourcePort = 1000, DestinationPort = 443,
        };
        var differentSocket = new TrafficEvent
        {
            Direction = TrafficDirection.Outbound, Protocol = TransportProtocol.TCP,
            SourcePort = 1001, DestinationPort = 443,
        };
        var differentProtocol = new TrafficEvent
        {
            Direction = TrafficDirection.Outbound, Protocol = TransportProtocol.UDP,
            SourcePort = 1000, DestinationPort = 443,
        };

        a.FlowKey.Should().NotBe(differentSocket.FlowKey);
        a.FlowKey.Should().NotBe(differentProtocol.FlowKey);
    }

    [Fact]
    public void RemoteAddress_MissingEndpoint_IsNull()
    {
        new TrafficEvent { Direction = TrafficDirection.Outbound }.RemoteAddress.Should().BeNull();
        new TrafficEvent { Direction = TrafficDirection.Inbound }.RemoteAddress.Should().BeNull();
    }

    [Fact]
    public void Hostname_WhenSet_RaisesPropertyChanged()
    {
        // Reverse DNS completes after the row is already displayed, so the back-fill
        // only reaches the UI if this notification fires.
        var evt = new TrafficEvent();
        var raised = new List<string?>();
        evt.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        evt.Hostname = "example.com";

        evt.Hostname.Should().Be("example.com");
        raised.Should().ContainSingle().Which.Should().Be(nameof(TrafficEvent.Hostname));
    }

    [Fact]
    public void Hostname_SetToSameValue_DoesNotRaise()
    {
        // Every buffered event sharing a peer is rewritten when a lookup lands, so
        // unchanged writes must stay silent rather than churn the bound rows.
        var evt = new TrafficEvent { Hostname = "example.com" };
        var raised = 0;
        evt.PropertyChanged += (_, _) => raised++;

        evt.Hostname = "example.com";

        raised.Should().Be(0);
    }

    [Fact]
    public void Hostname_ClearedToNull_RaisesPropertyChanged()
    {
        var evt = new TrafficEvent { Hostname = "example.com" };
        var raised = 0;
        evt.PropertyChanged += (_, _) => raised++;

        evt.Hostname = null;

        evt.Hostname.Should().BeNull();
        raised.Should().Be(1);
    }
}
