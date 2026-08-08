using System.ComponentModel;
using System.Net;

namespace WinFWManager.Core.Models;

public class TrafficEvent : INotifyPropertyChanged
{
    public DateTime Timestamp { get; set; }
    public TrafficDirection Direction { get; set; }
    public TransportProtocol Protocol { get; set; }
    public IPAddress? SourceAddress { get; set; }
    public int SourcePort { get; set; }
    public IPAddress? DestinationAddress { get; set; }
    public int DestinationPort { get; set; }
    public TrafficAction Action { get; set; }
    public int ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public string? InterfaceName { get; set; }
    public long InterfaceLuid { get; set; }
    public AdapterType AdapterType { get; set; } = AdapterType.Unknown;
    public FirewallProfile Profile { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public string? Asn { get; set; }
    /// <summary>
    /// Reverse-DNS name of the remote peer. Unlike the other fields this is filled in
    /// after the event is already on screen — the lookup runs in the background — so it
    /// raises a change notification to refresh the bound row.
    /// </summary>
    public string? Hostname
    {
        get => _hostname;
        set
        {
            if (_hostname == value) return;
            _hostname = value;
            PropertyChanged?.Invoke(this, HostnameChanged);
        }
    }

    private string? _hostname;
    private static readonly PropertyChangedEventArgs HostnameChanged = new(nameof(Hostname));

    public event PropertyChangedEventHandler? PropertyChanged;

    public long FilterId { get; set; }

    /// <summary>Human-readable drop reason; null for allowed traffic.</summary>
    public string? DropReason { get; set; }

    /// <summary>True when the adapter was resolved from an ETW IfIndex
    /// (authoritative); false when derived by IP/subnet matching.</summary>
    public bool IsInterfaceExact { get; set; }

    /// <summary>ETW interface index when the event carried one; resolved to an
    /// adapter during enrichment.</summary>
    public int? InterfaceIndexHint { get; set; }

    /// <summary>
    /// The far end of the connection: the destination when we sent it, the source when
    /// we received it. Enrichment, top talkers and the graph all key off this. Deriving
    /// it separately in each caller is what left the dashboard grouping inbound traffic
    /// by this machine's own address, so it lives here now.
    /// </summary>
    public IPAddress? RemoteAddress
        => Direction == TrafficDirection.Outbound ? DestinationAddress : SourceAddress;

    /// <summary>This machine's end of the connection — the mirror of <see cref="RemoteAddress"/>.</summary>
    public IPAddress? LocalAddress
        => Direction == TrafficDirection.Outbound ? SourceAddress : DestinationAddress;

    /// <summary>Compact flow path, e.g. "WSL guest → vEthernet (WSL) ⛔".</summary>
    public string FlowDescription
    {
        get
        {
            string nic = InterfaceName ?? "?";
            string sym = Action == TrafficAction.Allow ? "✓" : "⛔";
            if (Direction == TrafficDirection.Inbound)
            {
                string src = IsWslTraffic ? "WSL guest"
                    : IsHyperVTraffic ? "Hyper-V guest"
                    : IsSourcePrivate ? "LAN" : "internet";
                return $"{src} → {nic} {sym}";
            }
            string dst = IsWslTraffic ? "WSL guest"
                : IsHyperVTraffic ? "Hyper-V guest"
                : IsDestinationPrivate ? "LAN" : "internet";
            return $"{nic} → {dst} {sym}";
        }
    }

    public bool IsWslTraffic =>
        AdapterType == AdapterType.WSL
        || InterfaceName?.Contains("WSL", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsHyperVTraffic =>
        !IsWslTraffic
        && (AdapterType is AdapterType.HyperV or AdapterType.VSwitch
            || InterfaceName?.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) == true);

    public bool IsDestinationPrivate =>
        DestinationAddress != null && IsPrivateAddress(DestinationAddress);

    public bool IsSourcePrivate =>
        SourceAddress != null && IsPrivateAddress(SourceAddress);

    private static bool IsPrivateAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 127);
    }
}
