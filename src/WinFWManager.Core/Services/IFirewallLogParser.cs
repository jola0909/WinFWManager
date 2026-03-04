using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface IFirewallLogParser
{
    Task<IReadOnlyList<TrafficEvent>> ParseFileAsync(
        string filePath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
