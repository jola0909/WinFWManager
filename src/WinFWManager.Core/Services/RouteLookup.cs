using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinFWManager.Core.Services;

/// <summary>
/// Asks Windows which interface it would route a given peer over.
///
/// Needed because traffic from wildcard-bound sockets (0.0.0.0 / ::) carries no usable
/// local address — very common for outbound QUIC — so attributing it by IP or subnet
/// cannot work. On a test machine this accounted for a third of all captured events,
/// all of which showed a blank NIC.
/// </summary>
public static class RouteLookup
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetBestInterfaceEx(byte[] pDestAddr, out int pdwBestIfIndex);

    /// <summary>
    /// True when an address is absent or the "any" address, meaning the socket was bound
    /// to every interface and the endpoint tells us nothing about which one is in use.
    /// </summary>
    public static bool IsWildcard(IPAddress? address)
        => address == null
        || address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.IPv6Any);

    /// <summary>
    /// The interface index Windows would use to reach <paramref name="remote"/>, or null
    /// if the address family is unsupported or no route exists.
    /// </summary>
    public static int? GetBestInterfaceIndex(IPAddress remote)
    {
        var sockaddr = BuildSockAddr(remote);
        if (sockaddr == null)
            return null;

        try
        {
            // Returns NO_ERROR (0) on success; anything else means "no route".
            return GetBestInterfaceEx(sockaddr, out var index) == 0 && index > 0
                ? index
                : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a SOCKADDR_IN / SOCKADDR_IN6 for the address. The family is a little-endian
    /// USHORT in the first two bytes; the rest beyond the address stays zero, since
    /// GetBestInterfaceEx only looks at the family and address.
    /// </summary>
    public static byte[]? BuildSockAddr(IPAddress remote)
    {
        switch (remote.AddressFamily)
        {
            case AddressFamily.InterNetwork:
            {
                var sa = new byte[16];
                sa[0] = AfInet;
                remote.GetAddressBytes().CopyTo(sa, 4);   // sin_addr
                return sa;
            }

            case AddressFamily.InterNetworkV6:
            {
                var sa = new byte[28];
                sa[0] = AfInet6;
                remote.GetAddressBytes().CopyTo(sa, 8);   // sin6_addr
                BitConverter.GetBytes((uint)remote.ScopeId).CopyTo(sa, 24);
                return sa;
            }

            default:
                return null;
        }
    }
}
