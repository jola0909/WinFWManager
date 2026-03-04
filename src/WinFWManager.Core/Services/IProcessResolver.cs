using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface IProcessResolver
{
    ProcessInfo Resolve(int processId);
    void ClearCache();
}
