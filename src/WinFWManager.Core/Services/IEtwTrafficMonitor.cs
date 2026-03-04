using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface IEtwTrafficMonitor : IDisposable
{
    IObservable<TrafficEvent> TrafficEvents { get; }
    bool IsRunning { get; }
    bool RequiresAdmin { get; }
    void Start();
    void Stop();
}
