using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface INetworkInterfaceService
{
    Task<IReadOnlyList<NetworkAdapterInfo>> GetAllAdaptersAsync();
    Task RefreshAsync();
    string? ResolveInterfaceName(long interfaceLuid);
    string? ResolveInterfaceByIp(System.Net.IPAddress address);
    AdapterType ClassifyAdapter(string interfaceName);
}
