using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface INetworkInterfaceService
{
    Task<IReadOnlyList<NetworkAdapterInfo>> GetAllAdaptersAsync();
    Task RefreshAsync();
    string? ResolveInterfaceName(long interfaceLuid);
    AdapterType ClassifyAdapter(string interfaceName);
}
