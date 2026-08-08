using System.Net;
using System.Net.Sockets;

namespace WinFWManager.Core.Models;

public static class IpAddressUtils
{
    /// <summary>
    /// True for group and broadcast destinations — mDNS (224.0.0.251, ff02::fb),
    /// LLMNR (224.0.0.252), SSDP (239.255.255.250) and the limited broadcast address.
    ///
    /// These are destinations rather than peers: nothing is on the other end answering
    /// as a host, so ranking them alongside real endpoints crowds the list.
    /// </summary>
    public static bool IsMulticastOrBroadcast(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6Multicast;

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();

        // 224.0.0.0/4
        if (bytes[0] >= 224 && bytes[0] <= 239)
            return true;

        // 255.255.255.255 (limited broadcast)
        return bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255;
    }

    /// <summary>
    /// The address without any IPv6 scope suffix, for comparing an adapter's address
    /// ("fe80::1%3") against the same address as captured on the wire ("fe80::1").
    /// </summary>
    public static string ScopelessKey(IPAddress address)
        => new IPAddress(address.GetAddressBytes()).ToString();
}
