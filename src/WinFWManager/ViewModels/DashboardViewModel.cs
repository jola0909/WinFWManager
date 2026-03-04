using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WinFWManager.Core.Collections;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IEtwTrafficMonitor _etwMonitor;
    private readonly RingBuffer<TrafficEvent> _recentEvents = new(10_000);
    private IDisposable? _subscription;
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty] private int _totalConnections;
    [ObservableProperty] private int _blockedConnections;
    [ObservableProperty] private double _blockedPercent;
    [ObservableProperty] private int _allowedConnections;
    [ObservableProperty] private int _inboundCount;
    [ObservableProperty] private int _outboundCount;

    public ObservableCollection<TopTalkerEntry> TopTalkers { get; } = new();
    public ObservableCollection<TopTalkerEntry> TopBlocked { get; } = new();

    public DashboardViewModel(IEtwTrafficMonitor etwMonitor)
    {
        _etwMonitor = etwMonitor;

        _subscription = _etwMonitor.TrafficEvents
            .Buffer(TimeSpan.FromMilliseconds(500))
            .Where(batch => batch.Count > 0)
            .ObserveOn(System.Threading.SynchronizationContext.Current!)
            .Subscribe(OnEventBatch);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshStats();
        _refreshTimer.Start();
    }

    private void OnEventBatch(IList<TrafficEvent> batch)
    {
        foreach (var evt in batch)
            _recentEvents.Add(evt);
    }

    private void RefreshStats()
    {
        var events = _recentEvents.ToList();
        TotalConnections = events.Count;
        BlockedConnections = events.Count(e => e.Action is TrafficAction.Block or TrafficAction.Drop);
        AllowedConnections = events.Count(e => e.Action == TrafficAction.Allow);
        BlockedPercent = TotalConnections > 0 ? (double)BlockedConnections / TotalConnections * 100 : 0;
        InboundCount = events.Count(e => e.Direction == TrafficDirection.Inbound);
        OutboundCount = events.Count(e => e.Direction == TrafficDirection.Outbound);

        // Top talkers by destination IP
        var topTalkers = events
            .Where(e => e.DestinationAddress != null)
            .GroupBy(e => e.DestinationAddress!.ToString())
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => new TopTalkerEntry
            {
                Address = g.Key,
                Count = g.Count(),
                Country = g.First().Country ?? "Unknown"
            })
            .ToList();

        TopTalkers.Clear();
        foreach (var t in topTalkers)
            TopTalkers.Add(t);

        // Top blocked destinations
        var topBlocked = events
            .Where(e => e.Action is TrafficAction.Block or TrafficAction.Drop && e.DestinationAddress != null)
            .GroupBy(e => e.DestinationAddress!.ToString())
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => new TopTalkerEntry
            {
                Address = g.Key,
                Count = g.Count(),
                Country = g.First().Country ?? "Unknown"
            })
            .ToList();

        TopBlocked.Clear();
        foreach (var t in topBlocked)
            TopBlocked.Add(t);
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _subscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class TopTalkerEntry
{
    public string Address { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Country { get; set; } = "Unknown";
}
