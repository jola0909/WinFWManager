using System.Net;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class NetworkInterfaceServiceTests
{
    private static readonly NetworkAdapterInfo Physical = new()
    {
        Name = "Ethernet",
        AdapterType = AdapterType.Physical,
        IpAddresses = { IPAddress.Parse("192.168.1.10") },
        Subnets = { new IpSubnet(IPAddress.Parse("192.168.1.10"), 24) },
    };

    private static readonly NetworkAdapterInfo Wsl = new()
    {
        Name = "vEthernet (WSL)",
        AdapterType = AdapterType.WSL,
        IpAddresses = { IPAddress.Parse("172.20.0.1") },
        Subnets = { new IpSubnet(IPAddress.Parse("172.20.0.1"), 20) },
    };

    private static readonly NetworkAdapterInfo HyperV = new()
    {
        Name = "vEthernet (Default Switch)",
        AdapterType = AdapterType.VSwitch,
        IpAddresses = { IPAddress.Parse("172.30.0.1") },
        Subnets = { new IpSubnet(IPAddress.Parse("172.30.0.1"), 20) },
    };

    private static readonly IReadOnlyList<NetworkAdapterInfo> Adapters = new[] { Physical, Wsl, HyperV };

    [Fact]
    public void ResolveAdapter_LocalExactMatch_ReturnsOwningAdapter()
    {
        var a = NetworkInterfaceService.ResolveAdapterFrom(
            Adapters, IPAddress.Parse("192.168.1.10"), IPAddress.Parse("8.8.8.8"));
        a.Should().BeSameAs(Physical);
    }

    [Fact]
    public void ResolveAdapter_LocalInPhysicalSubnet_ReturnsPhysical()
    {
        var a = NetworkInterfaceService.ResolveAdapterFrom(
            Adapters, IPAddress.Parse("192.168.1.55"), IPAddress.Parse("8.8.8.8"));
        a.Should().BeSameAs(Physical);
    }

    [Fact]
    public void ResolveAdapter_RemotePeerInWslSubnet_ReturnsWsl()
    {
        // Host<->WSL: the local endpoint isn't a known adapter address, but the
        // peer (WSL guest) IP falls inside the WSL adapter's subnet.
        var a = NetworkInterfaceService.ResolveAdapterFrom(
            Adapters, IPAddress.Parse("100.100.100.100"), IPAddress.Parse("172.20.15.9"));
        a.Should().BeSameAs(Wsl);
    }

    [Fact]
    public void ResolveAdapter_RemotePeerInHyperVSubnet_ReturnsHyperV()
    {
        var a = NetworkInterfaceService.ResolveAdapterFrom(
            Adapters, null, IPAddress.Parse("172.30.4.5"));
        a.Should().BeSameAs(HyperV);
    }

    [Fact]
    public void ResolveAdapter_NoMatch_ReturnsNull()
    {
        var a = NetworkInterfaceService.ResolveAdapterFrom(
            Adapters, IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1"));
        a.Should().BeNull();
    }

    [Fact]
    public void ClassifyAdapter_WSL_ReturnsWSL()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("vEthernet (WSL)").Should().Be(AdapterType.WSL);
    }

    [Fact]
    public void ClassifyAdapter_HyperVSwitch_ReturnsVSwitch()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("vEthernet (Default Switch)").Should().Be(AdapterType.VSwitch);
    }

    [Fact]
    public void ClassifyAdapter_Physical_ReturnsPhysical()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("Ethernet").Should().Be(AdapterType.Physical);
    }

    [Fact]
    public void ClassifyAdapter_Loopback_ReturnsLoopback()
    {
        var svc = new NetworkInterfaceService();
        svc.ClassifyAdapter("Loopback Pseudo-Interface 1").Should().Be(AdapterType.Loopback);
    }

    [Fact]
    public async Task GetAllAdaptersAsync_ReturnsAtLeastOne()
    {
        var svc = new NetworkInterfaceService();
        var adapters = await svc.GetAllAdaptersAsync();
        adapters.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_DoesNotThrow()
    {
        var svc = new NetworkInterfaceService();
        var act = () => svc.RefreshAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void ResolveByIfIndex_KnownIndex_ReturnsAdapter()
    {
        var adapters = new[]
        {
            new NetworkAdapterInfo { Name = "Ethernet", InterfaceIndex = 12 },
            new NetworkAdapterInfo { Name = "vEthernet (WSL)", InterfaceIndex = 33 },
        };
        NetworkInterfaceService.ResolveByIfIndexFrom(adapters, 33)!.Name
            .Should().Be("vEthernet (WSL)");
    }

    [Fact]
    public void ResolveByIfIndex_UnknownIndex_ReturnsNull()
    {
        var adapters = new[] { new NetworkAdapterInfo { Name = "Ethernet", InterfaceIndex = 12 } };
        NetworkInterfaceService.ResolveByIfIndexFrom(adapters, 99).Should().BeNull();
    }

    [Theory]
    [InlineData("Ethernet 2-WFP Native MAC Layer LightWeight Filter-0000")]
    [InlineData("Ethernet 2-WFP 802.3 MAC Layer LightWeight Filter-0000")]
    [InlineData("Ethernet 2-QoS Packet Scheduler-0000")]
    public void LooksLikePseudoAdapter_FilterBindings_AreHidden(string name)
    {
        NetworkInterfaceService.LooksLikePseudoAdapter(name, "").Should().BeTrue();
    }

    [Fact]
    public void LooksLikePseudoAdapter_LocalizedNameWithEnglishDescription_IsHidden()
    {
        // On a localized Windows the connection name is translated, but the driver
        // description keeps the English marker — so matching must consider both.
        NetworkInterfaceService.LooksLikePseudoAdapter(
            "Anslutning till lokalt nätverk* 6",
            "WAN Miniport (IP)-WFP Native MAC Layer LightWeight Filter-0000")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("Ethernet 2", "Realtek PCIe 2.5GbE Family Controller")]
    [InlineData("Wi-Fi", "Realtek 8922AE WiFi 7 PCI-E NIC")]
    [InlineData("Loopback Pseudo-Interface 1", "Software Loopback Interface 1")]
    public void LooksLikePseudoAdapter_RealAdapters_AreNotHidden(string name, string description)
    {
        NetworkInterfaceService.LooksLikePseudoAdapter(name, description).Should().BeFalse();
    }

    [Fact]
    public void LooksLikePseudoAdapter_WslAdapter_IsNotHidden()
    {
        // Regression guard: the WSL adapter carries traffic we care about and must
        // never be filtered out of the UI.
        NetworkInterfaceService.LooksLikePseudoAdapter(
            "vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter")
            .Should().BeFalse();
    }

    [Fact]
    public void LooksLikePseudoAdapter_NullOrEmpty_IsNotHidden()
    {
        NetworkInterfaceService.LooksLikePseudoAdapter(null, null).Should().BeFalse();
        NetworkInterfaceService.LooksLikePseudoAdapter("", "").Should().BeFalse();
    }

    private static readonly Guid RealGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PseudoGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void IsHiddenAdapter_InCimSet_IsVisible()
    {
        var cim = new HashSet<Guid> { RealGuid };
        NetworkInterfaceService.IsHiddenAdapter(
            RealGuid, "Ethernet 2", "Realtek PCIe 2.5GbE", AdapterType.Physical, cim)
            .Should().BeFalse();
    }

    [Fact]
    public void IsHiddenAdapter_AbsentFromCimSet_IsHidden()
    {
        // Windows does not list it in MSFT_NetAdapter, so it is a pseudo-adapter —
        // even though nothing in its name says so.
        var cim = new HashSet<Guid> { RealGuid };
        NetworkInterfaceService.IsHiddenAdapter(
            PseudoGuid, "Wi-Fi 2", "Realtek 8922AE WiFi 7 PCI-E NIC #2", AdapterType.Physical, cim)
            .Should().BeTrue();
    }

    [Fact]
    public void IsHiddenAdapter_Loopback_IsVisibleEvenWhenAbsentFromCim()
    {
        // Loopback never appears in MSFT_NetAdapter, but 127.0.0.1 traffic is real.
        var cim = new HashSet<Guid> { RealGuid };
        NetworkInterfaceService.IsHiddenAdapter(
            PseudoGuid, "Loopback Pseudo-Interface 1", "Software Loopback Interface 1",
            AdapterType.Loopback, cim)
            .Should().BeFalse();
    }

    [Fact]
    public void IsHiddenAdapter_NoCimData_FallsBackToNameHeuristic()
    {
        var empty = new HashSet<Guid>();

        NetworkInterfaceService.IsHiddenAdapter(
            PseudoGuid, "Ethernet 2-QoS Packet Scheduler-0000",
            "Realtek-QoS Packet Scheduler-0000", AdapterType.Physical, empty)
            .Should().BeTrue();

        NetworkInterfaceService.IsHiddenAdapter(
            RealGuid, "Ethernet 2", "Realtek PCIe 2.5GbE", AdapterType.Physical, empty)
            .Should().BeFalse();
    }

    [Fact]
    public void IsHiddenAdapter_NoCimData_KeepsWslVisible()
    {
        // Degrading to the heuristic must never hide the WSL adapter.
        NetworkInterfaceService.IsHiddenAdapter(
            PseudoGuid, "vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter",
            AdapterType.WSL, new HashSet<Guid>())
            .Should().BeFalse();
    }
}
