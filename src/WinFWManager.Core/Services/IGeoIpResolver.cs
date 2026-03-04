using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface IGeoIpResolver : IDisposable
{
    GeoInfo Resolve(IPAddress address);
    Task<string?> ReverseDnsAsync(IPAddress address);
}
