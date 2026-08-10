using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class NetworkInterfaceService : INetworkInterfaceService, IDisposable
{
    private List<NetworkAdapterInfo> _adapters = new();
    private readonly ConcurrentDictionary<long, string> _luidToName = new();
    private readonly ConcurrentDictionary<string, string> _ipToName = new(StringComparer.Ordinal);
    private readonly Lazy<CimNetAdapterQueryService?> _cim = new(() =>
    {
        try { return new CimNetAdapterQueryService(); }
        catch { return null; }
    });

    public NetworkInterfaceService()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged +=
            (_, _) => { try { RefreshAsync(); } catch { } };
    }

    public async Task<IReadOnlyList<NetworkAdapterInfo>> GetAllAdaptersAsync()
    {
        await RefreshAsync();
        return _adapters.AsReadOnly();
    }

    public Task RefreshAsync()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var adapters = new List<NetworkAdapterInfo>();

        // Authoritative "real adapter" set from CIM; empty when WMI is unavailable, in
        // which case we fall back to matching driver-supplied pseudo-adapter markers.
        IReadOnlySet<Guid> cimVisible;
        try
        {
            cimVisible = _cim.Value?.GetVisibleAdapterGuids() ?? new HashSet<Guid>();
        }
        catch
        {
            cimVisible = new HashSet<Guid>();
        }

        foreach (var ni in interfaces)
        {
            var props = ni.GetIPProperties();
            var unicast = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                         || a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                .ToList();

            var addresses = unicast.Select(a => a.Address).ToList();
            var subnets = unicast
                .Where(a => a.PrefixLength > 0)
                .Select(a => new IpSubnet(a.Address, a.PrefixLength))
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

            Guid.TryParse(ni.Id, out var interfaceGuid);
            var adapterType = ClassifyAdapter(ni.Name, ni.Description);

            var adapter = new NetworkAdapterInfo
            {
                Name = ni.Name,
                InterfaceAlias = ni.Description,
                InterfaceGuid = interfaceGuid,
                AdapterType = adapterType,
                Status = ni.OperationalStatus.ToString(),
                IpAddresses = addresses,
                Subnets = subnets,
                MacAddress = FormatMac(ni.GetPhysicalAddress()),
                InterfaceIndex = interfaceIndex,
                IsHidden = IsHiddenAdapter(
                    interfaceGuid, ni.Name, ni.Description, adapterType, cimVisible)
            };

            adapters.Add(adapter);
        }

        _adapters = adapters;

        // Build IP → adapter name lookup
        _ipToName.Clear();
        foreach (var a in adapters)
        {
            foreach (var ip in a.IpAddresses)
                _ipToName[ip.ToString()] = a.Name;
        }

        return Task.CompletedTask;
    }

    public string? ResolveInterfaceName(long interfaceLuid)
    {
        if (_luidToName.TryGetValue(interfaceLuid, out var name))
            return name;

        return null;
    }

    public string? ResolveInterfaceByIp(IPAddress address)
    {
        if (_ipToName.TryGetValue(address.ToString(), out var name))
            return name;

        return null;
    }

    /// <summary>
    /// Resolves the adapter a connection traverses, given its local and remote
    /// endpoints. Kernel TCP/IP ETW events carry no interface identifier, so we
    /// attribute traffic by IP: an exact match on the local endpoint, then a
    /// most-specific subnet match on the local endpoint, then on the remote
    /// endpoint. The last step is what catches host&lt;-&gt;WSL/Hyper-V traffic,
    /// where the peer (guest) IP lives in a virtual adapter's subnet.
    /// </summary>
    public NetworkAdapterInfo? ResolveAdapter(IPAddress? local, IPAddress? remote)
        => ResolveAdapterFrom(_adapters, local, remote);

    /// <summary>Pure resolution logic over a supplied adapter list (unit-testable).</summary>
    public static NetworkAdapterInfo? ResolveAdapterFrom(
        IReadOnlyList<NetworkAdapterInfo> adapters, IPAddress? local, IPAddress? remote)
    {
        // 1. Exact match: the local endpoint is one of an adapter's own addresses.
        if (local != null)
        {
            foreach (var a in adapters)
                if (a.IpAddresses.Any(ip => ip.Equals(local)))
                    return a;
        }

        // 2. Most-specific subnet containing the local endpoint.
        var byLocal = MatchSubnet(adapters, local);
        if (byLocal != null) return byLocal;

        // 3. Most-specific subnet containing the remote endpoint (host<->VM peer).
        return MatchSubnet(adapters, remote);
    }

    /// <summary>
    /// Resolves an adapter by its interface index, as carried in TCPIP-provider
    /// ETW drop events. Returns null when the index is unknown or non-positive.
    /// </summary>
    public NetworkAdapterInfo? ResolveByIfIndex(int ifIndex)
        => ResolveByIfIndexFrom(_adapters, ifIndex);

    /// <summary>Pure resolution logic over a supplied adapter list (unit-testable).</summary>
    public static NetworkAdapterInfo? ResolveByIfIndexFrom(
        IReadOnlyList<NetworkAdapterInfo> adapters, int ifIndex)
        => ifIndex <= 0 ? null : adapters.FirstOrDefault(a => a.InterfaceIndex == ifIndex);

    private static NetworkAdapterInfo? MatchSubnet(
        IReadOnlyList<NetworkAdapterInfo> adapters, IPAddress? address)
    {
        if (address == null) return null;

        NetworkAdapterInfo? best = null;
        int bestPrefix = -1;
        foreach (var a in adapters)
            foreach (var s in a.Subnets)
                if (s.PrefixLength > bestPrefix && s.Contains(address))
                {
                    best = a;
                    bestPrefix = s.PrefixLength;
                }

        return best;
    }

    /// <summary>
    /// Driver-supplied markers for NDIS pseudo-adapters. These strings come from the
    /// filter/miniport drivers rather than the Windows UI, so they stay in English on
    /// localized installs — unlike the connection name, which does not.
    /// Only used when CIM cannot supply the authoritative <c>Hidden</c> flag.
    /// </summary>
    private static readonly string[] PseudoAdapterMarkers =
    {
        "LightWeight Filter",
        "QoS Packet Scheduler",
        "WFP Native MAC Layer",
        "WFP 802.3 MAC Layer",
        "Native WiFi Filter Driver",
        "Virtual Switch Extension",
        "Virtual Filtering Platform",
        "WAN Miniport",
        "Kernel Debug Network Adapter",
        "Teredo Tunneling",
        "IP-HTTPS",
        "6to4",
        "ISATAP",
    };

    /// <summary>
    /// Decides whether an adapter should be hidden from the UI by default.
    ///
    /// Prefers CIM: an adapter Windows itself does not list in <c>MSFT_NetAdapter</c> is
    /// a pseudo-adapter. Falls back to name matching only when CIM returned nothing, so a
    /// WMI failure degrades to "show a bit too much" rather than "hide everything".
    /// Loopback is always shown — it never appears in <c>MSFT_NetAdapter</c>, but
    /// 127.0.0.1 traffic is real and worth monitoring.
    /// </summary>
    public static bool IsHiddenAdapter(
        Guid interfaceGuid, string? name, string? description,
        AdapterType adapterType, IReadOnlySet<Guid> cimVisibleGuids)
    {
        if (adapterType == AdapterType.Loopback)
            return false;

        if (cimVisibleGuids.Count > 0)
            return !cimVisibleGuids.Contains(interfaceGuid);

        return LooksLikePseudoAdapter(name, description);
    }

    /// <summary>
    /// Heuristic fallback for <see cref="NetworkAdapterInfo.IsHidden"/> when CIM is
    /// unavailable. Matches on both the connection name and the adapter description,
    /// since the filter suffix can appear on either.
    /// </summary>
    public static bool LooksLikePseudoAdapter(string? name, string? description)
    {
        foreach (var marker in PseudoAdapterMarkers)
        {
            if (name?.Contains(marker, StringComparison.OrdinalIgnoreCase) == true)
                return true;
            if (description?.Contains(marker, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return false;
    }

    public AdapterType ClassifyAdapter(string interfaceName)
        => ClassifyAdapter(interfaceName, null);

    /// <summary>
    /// Classifies an adapter from its connection name and driver description.
    ///
    /// Both are needed because only one of them is stable across languages. The
    /// connection name is localized — a Swedish install reports
    /// "Bluetooth-nätverksanslutning 2" — while the description comes from the driver and
    /// stays English ("Hyper-V Virtual Ethernet Adapter"). Matching the name alone
    /// silently degrades every virtual adapter to Physical on a non-English system.
    /// </summary>
    public AdapterType ClassifyAdapter(string? interfaceName, string? description)
    {
        if (string.IsNullOrEmpty(interfaceName) && string.IsNullOrEmpty(description))
            return AdapterType.Unknown;

        if (Mentions("Loopback"))
            return AdapterType.Loopback;

        // WSL before Hyper-V: the WSL adapter's description is the generic Hyper-V one,
        // so checking Hyper-V first would swallow it.
        if (Mentions("WSL"))
            return AdapterType.WSL;

        if (StartsWithInName("vEthernet"))
            return AdapterType.VSwitch;

        if (Mentions("Hyper-V"))
            return AdapterType.HyperV;

        if (StartsWithInName("vSwitch"))
            return AdapterType.VSwitch;

        if (Mentions("Virtual") || Mentions("VPN") || Mentions("TAP"))
            return AdapterType.Virtual;

        return AdapterType.Physical;

        bool Mentions(string token)
            => interfaceName?.Contains(token, StringComparison.OrdinalIgnoreCase) == true
            || description?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

        bool StartsWithInName(string token)
            => interfaceName?.StartsWith(token, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? FormatMac(PhysicalAddress mac)
    {
        var bytes = mac.GetAddressBytes();
        if (bytes.Length == 0) return null;
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    public void Dispose()
    {
        if (_cim.IsValueCreated)
            _cim.Value?.Dispose();
    }
}
