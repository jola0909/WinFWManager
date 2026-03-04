using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class NetworkInterfaceService : INetworkInterfaceService
{
    private List<NetworkAdapterInfo> _adapters = new();
    private readonly ConcurrentDictionary<long, string> _luidToName = new();

    public async Task<IReadOnlyList<NetworkAdapterInfo>> GetAllAdaptersAsync()
    {
        await RefreshAsync();
        return _adapters.AsReadOnly();
    }

    public Task RefreshAsync()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var adapters = new List<NetworkAdapterInfo>();

        foreach (var ni in interfaces)
        {
            var props = ni.GetIPProperties();
            var addresses = props.UnicastAddresses
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                         || a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                .ToList();

            int interfaceIndex = 0;
            try
            {
                var ipv4Props = props.GetIPv4Properties();
                interfaceIndex = ipv4Props?.Index ?? 0;
            }
            catch
            {
                // Some adapters don't support IPv4 properties
            }

            var adapter = new NetworkAdapterInfo
            {
                Name = ni.Name,
                InterfaceAlias = ni.Description,
                AdapterType = ClassifyAdapter(ni.Name),
                Status = ni.OperationalStatus.ToString(),
                IpAddresses = addresses,
                MacAddress = FormatMac(ni.GetPhysicalAddress()),
                InterfaceIndex = interfaceIndex
            };

            adapters.Add(adapter);
        }

        _adapters = adapters;
        return Task.CompletedTask;
    }

    public string? ResolveInterfaceName(long interfaceLuid)
    {
        if (_luidToName.TryGetValue(interfaceLuid, out var name))
            return name;

        return null;
    }

    public AdapterType ClassifyAdapter(string interfaceName)
    {
        if (string.IsNullOrEmpty(interfaceName))
            return AdapterType.Unknown;

        if (interfaceName.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
            return AdapterType.Loopback;

        if (interfaceName.Contains("WSL", StringComparison.OrdinalIgnoreCase))
            return AdapterType.WSL;

        if (interfaceName.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
            return AdapterType.VSwitch;

        if (interfaceName.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            return AdapterType.HyperV;

        if (interfaceName.StartsWith("vSwitch", StringComparison.OrdinalIgnoreCase))
            return AdapterType.VSwitch;

        if (interfaceName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || interfaceName.Contains("VPN", StringComparison.OrdinalIgnoreCase)
            || interfaceName.Contains("TAP", StringComparison.OrdinalIgnoreCase))
            return AdapterType.Virtual;

        return AdapterType.Physical;
    }

    private static string? FormatMac(PhysicalAddress mac)
    {
        var bytes = mac.GetAddressBytes();
        if (bytes.Length == 0) return null;
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}
