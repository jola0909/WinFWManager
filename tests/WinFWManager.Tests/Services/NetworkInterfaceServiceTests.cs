using FluentAssertions;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class NetworkInterfaceServiceTests
{
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
}
