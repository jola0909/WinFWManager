using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinFWManager.Core.Collections;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class TrafficMonitorViewModel : ObservableObject, IDisposable
{
    private readonly IEtwTrafficMonitor _etwMonitor;
    private readonly IProcessResolver _processResolver;
    private readonly IGeoIpResolver _geoIpResolver;
    private readonly RingBuffer<TrafficEvent> _eventBuffer = new(50_000);
    private IDisposable? _subscription;
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<TrafficEvent> Events { get; } = new();
    public ICollectionView EventsView { get; }

    [ObservableProperty] private string _filterSourceIp = string.Empty;
    [ObservableProperty] private string _filterDestIp = string.Empty;
    [ObservableProperty] private string _filterProtocol = string.Empty;
    [ObservableProperty] private string _filterProcess = string.Empty;
    [ObservableProperty] private string _filterNic = string.Empty;
    [ObservableProperty] private bool _isAutoScroll = true;
    [ObservableProperty] private int _eventCount;

    public TrafficMonitorViewModel(
        IEtwTrafficMonitor etwMonitor,
        IProcessResolver processResolver,
        IGeoIpResolver geoIpResolver)
    {
        _etwMonitor = etwMonitor;
        _processResolver = processResolver;
        _geoIpResolver = geoIpResolver;
        _dispatcher = Dispatcher.CurrentDispatcher;

        EventsView = CollectionViewSource.GetDefaultView(Events);
        EventsView.Filter = FilterPredicate;

        _subscription = _etwMonitor.TrafficEvents
            .Buffer(TimeSpan.FromMilliseconds(100))
            .Where(batch => batch.Count > 0)
            .ObserveOn(System.Threading.SynchronizationContext.Current!)
            .Subscribe(OnEventBatch);
    }

    private void OnEventBatch(IList<TrafficEvent> batch)
    {
        foreach (var evt in batch)
        {
            // Enrich with process and geo info
            var processInfo = _processResolver.Resolve(evt.ProcessId);
            evt.ProcessName = processInfo.DisplayName;

            if (evt.DestinationAddress != null)
            {
                var geoInfo = _geoIpResolver.Resolve(evt.DestinationAddress);
                evt.Country = geoInfo.DisplayCountry;
            }

            _eventBuffer.Add(evt);
            Events.Add(evt);

            // Keep display collection manageable
            while (Events.Count > 5000)
                Events.RemoveAt(0);
        }

        EventCount = _eventBuffer.Count;
    }

    partial void OnFilterSourceIpChanged(string value) => EventsView.Refresh();
    partial void OnFilterDestIpChanged(string value) => EventsView.Refresh();
    partial void OnFilterProtocolChanged(string value) => EventsView.Refresh();
    partial void OnFilterProcessChanged(string value) => EventsView.Refresh();
    partial void OnFilterNicChanged(string value) => EventsView.Refresh();

    private bool FilterPredicate(object obj)
    {
        if (obj is not TrafficEvent evt) return false;

        if (!string.IsNullOrEmpty(FilterSourceIp) &&
            evt.SourceAddress?.ToString().Contains(FilterSourceIp, StringComparison.OrdinalIgnoreCase) != true)
            return false;

        if (!string.IsNullOrEmpty(FilterDestIp) &&
            evt.DestinationAddress?.ToString().Contains(FilterDestIp, StringComparison.OrdinalIgnoreCase) != true)
            return false;

        if (!string.IsNullOrEmpty(FilterProtocol) &&
            !evt.Protocol.ToString().Contains(FilterProtocol, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(FilterProcess) &&
            evt.ProcessName?.Contains(FilterProcess, StringComparison.OrdinalIgnoreCase) != true)
            return false;

        if (!string.IsNullOrEmpty(FilterNic) &&
            evt.InterfaceName?.Contains(FilterNic, StringComparison.OrdinalIgnoreCase) != true)
            return false;

        return true;
    }

    [ObservableProperty] private TrafficEvent? _selectedEvent;

    [RelayCommand]
    private void ClearEvents()
    {
        Events.Clear();
        _eventBuffer.Clear();
        EventCount = 0;
    }

    [RelayCommand]
    private void CreateRuleFromTraffic()
    {
        if (SelectedEvent == null) return;

        var evt = SelectedEvent;
        var rule = new FirewallRuleInfo
        {
            DisplayName = $"Block {evt.DestinationAddress}:{evt.DestinationPort}",
            Name = $"WinFW_Block_{evt.DestinationAddress}_{evt.DestinationPort}_{DateTime.Now.Ticks}",
            Direction = evt.Direction,
            Action = TrafficAction.Block,
            Protocol = evt.Protocol,
            RemoteAddress = evt.DestinationAddress?.ToString(),
            RemotePort = evt.DestinationPort > 0 ? evt.DestinationPort.ToString() : null,
            Profile = evt.Profile,
            Enabled = true,
            IsHyperVRule = evt.IsHyperVTraffic
        };

        var dialog = new Views.RuleEditorDialog(rule);
        dialog.ShowDialog();
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}
