using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Fallback GeoIP resolver when MaxMind DLL is unavailable (e.g. blocked by WDAC).
/// Returns empty results for all lookups.
/// </summary>
public class NullGeoIpResolver : IGeoIpResolver
{
    public GeoInfo Resolve(IPAddress address) => new() { IsPrivate = IsPrivate(address) };

    public Task<string?> ReverseDnsAsync(IPAddress address) => Task.FromResult<string?>(null);

    public void Dispose() { }

    private static bool IsPrivate(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return address.Equals(IPAddress.IPv6Loopback);
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || bytes[0] == 127;
    }
}
