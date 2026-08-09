using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;
using System.Text;
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
    private readonly INetworkInterfaceService _nicService;
    private readonly RingBuffer<TrafficEvent> _eventBuffer = new(50_000);
    private readonly TrafficEventFilter _filter = new();
    private IDisposable? _subscription;
    private readonly Dispatcher _dispatcher;
    private bool _nicCacheLoaded;

    // Reverse-DNS results keyed by peer address. A completed lookup that found nothing
    // is stored as null, so a name that does not resolve is not retried on every batch.
    private readonly Dictionary<string, string?> _hostnameByIp = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hostnameLookupsInFlight = new(StringComparer.Ordinal);

    // DNS is slow relative to capture, so lookups are throttled and capped. Traffic can
    // touch thousands of distinct peers; resolving every one would achieve nothing but
    // load. Names already known are applied instantly from the cache.
    private readonly SemaphoreSlim _dnsThrottle = new(4, 4);
    private const int MaxTrackedHostnames = 2_000;

    public ObservableCollection<TrafficEvent> Events { get; } = new();
    public ICollectionView EventsView { get; }

    [ObservableProperty] private string _filterSourceIp = string.Empty;
    [ObservableProperty] private string _filterSrcPort = string.Empty;
    [ObservableProperty] private string _filterDestIp = string.Empty;
    [ObservableProperty] private string _filterDstPort = string.Empty;
    [ObservableProperty] private string _filterProtocol = string.Empty;
    [ObservableProperty] private string _filterProcess = string.Empty;
    [ObservableProperty] private string _filterNic = string.Empty;
    [ObservableProperty] private string _filterAction = string.Empty;
    [ObservableProperty] private bool _isAutoScroll = true;
    [ObservableProperty] private int _eventCount;
    [ObservableProperty] private bool _showMirroredBanner;

    public TrafficMonitorViewModel(
        IEtwTrafficMonitor etwMonitor,
        IProcessResolver processResolver,
        IGeoIpResolver geoIpResolver,
        INetworkInterfaceService nicService,
        WslNetworkModeDetector wslDetector)
    {
        _etwMonitor = etwMonitor;
        _processResolver = processResolver;
        _geoIpResolver = geoIpResolver;
        _nicService = nicService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        ShowMirroredBanner = wslDetector.DetectMode() == WslNetworkingMode.Mirrored;

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
        // Lazy-load NIC cache on first batch
        if (!_nicCacheLoaded)
        {
            _nicCacheLoaded = true;
            _ = _nicService.RefreshAsync();
        }

        foreach (var evt in batch)
        {
            // Enrich with process and geo info. PID 0 means "unknown" (e.g.
            // packet drops carry no PID) — resolving it would misleadingly
            // show the kernel Idle process, so leave the name empty instead.
            if (evt.ProcessId > 0)
            {
                var processInfo = _processResolver.Resolve(evt.ProcessId);
                evt.ProcessName = processInfo.DisplayName;
            }

            var localAddress = evt.Direction == TrafficDirection.Outbound
                ? evt.SourceAddress : evt.DestinationAddress;
            var remoteAddress = PeerAddress(evt);

            // Geo-locate the peer, not the destination: on an inbound event the
            // destination is this machine, which reported "Private" for every
            // connection arriving from the internet.
            if (remoteAddress != null)
            {
                var geoInfo = _geoIpResolver.Resolve(remoteAddress);
                evt.Country = geoInfo.DisplayCountry;
            }

            // Resolve NIC: IfIndex from ETW is authoritative; otherwise match
            // the local endpoint (and remote peer for host<->VM traffic) by IP.
            NetworkAdapterInfo? adapter = null;
            if (evt.InterfaceIndexHint is int ifIndex)
                adapter = _nicService.ResolveByIfIndex(ifIndex);
            if (adapter != null)
            {
                evt.IsInterfaceExact = true;
            }
            else
            {
                adapter = _nicService.ResolveAdapter(localAddress, remoteAddress);

                // Sockets bound to the wildcard address (0.0.0.0 / ::) carry no usable
                // local IP — common for outbound QUIC — so nothing above can match.
                // Ask Windows which interface it would actually route the peer over.
                if (adapter == null && RouteLookup.IsWildcard(localAddress) && remoteAddress != null)
                {
                    var routedIndex = RouteLookup.GetBestInterfaceIndex(remoteAddress);
                    if (routedIndex is int idx)
                        adapter = _nicService.ResolveByIfIndex(idx);
                }
            }
            if (adapter != null)
            {
                evt.InterfaceName = adapter.Name;
                evt.AdapterType = adapter.AdapterType;
            }

            // Apply a known name immediately; otherwise queue a lookup that back-fills
            // this row and every other one sharing the peer once it completes.
            if (remoteAddress != null && !RouteLookup.IsWildcard(remoteAddress))
            {
                var key = remoteAddress.ToString();
                if (_hostnameByIp.TryGetValue(key, out var known))
                    evt.Hostname = known;
                else
                    _ = ResolveHostnameAsync(remoteAddress, key);
            }

            _eventBuffer.Add(evt);
            Events.Add(evt);

            // Keep display collection manageable
            while (Events.Count > 5000)
                Events.RemoveAt(0);
        }

        EventCount = _eventBuffer.Count;
    }

    partial void OnFilterSourceIpChanged(string value) { _filter.SourceIp = value; EventsView.Refresh(); }
    partial void OnFilterSrcPortChanged(string value) { _filter.SrcPort = value; EventsView.Refresh(); }
    partial void OnFilterDestIpChanged(string value) { _filter.DestIp = value; EventsView.Refresh(); }
    partial void OnFilterDstPortChanged(string value) { _filter.DstPort = value; EventsView.Refresh(); }
    partial void OnFilterProtocolChanged(string value) { _filter.Protocol = value; EventsView.Refresh(); }
    partial void OnFilterProcessChanged(string value) { _filter.Process = value; EventsView.Refresh(); }
    partial void OnFilterNicChanged(string value) { _filter.Nic = value; EventsView.Refresh(); }
    partial void OnFilterActionChanged(string value) { _filter.Action = value; EventsView.Refresh(); }

    private bool FilterPredicate(object obj)
        => obj is TrafficEvent evt && _filter.Matches(evt);

    [ObservableProperty] private TrafficEvent? _selectedEvent;

    [RelayCommand]
    private void ClearEvents()
    {
        Events.Clear();
        _eventBuffer.Clear();
        EventCount = 0;
    }

    [RelayCommand]
    private void FilterBySourceIp()
    {
        if (SelectedEvent?.SourceAddress != null)
            FilterSourceIp = SelectedEvent.SourceAddress.ToString();
    }

    [RelayCommand]
    private void FilterByDestIp()
    {
        if (SelectedEvent?.DestinationAddress != null)
            FilterDestIp = SelectedEvent.DestinationAddress.ToString();
    }

    [RelayCommand]
    private void FilterBySrcPort()
    {
        if (SelectedEvent != null)
            FilterSrcPort = SelectedEvent.SourcePort.ToString();
    }

    [RelayCommand]
    private void FilterByDstPort()
    {
        if (SelectedEvent != null)
            FilterDstPort = SelectedEvent.DestinationPort.ToString();
    }

    [RelayCommand]
    private void FilterByProtocol()
    {
        if (SelectedEvent != null)
            FilterProtocol = SelectedEvent.Protocol.ToString();
    }

    [RelayCommand]
    private void FilterByProcess()
    {
        if (SelectedEvent?.ProcessName != null)
            FilterProcess = SelectedEvent.ProcessName;
    }

    [RelayCommand]
    private void FilterByNic()
    {
        if (SelectedEvent?.InterfaceName != null)
            FilterNic = SelectedEvent.InterfaceName;
    }

    [RelayCommand]
    private void FilterByAction()
    {
        if (SelectedEvent != null)
            FilterAction = SelectedEvent.Action.ToString();
    }

    [RelayCommand]
    private void ExcludeSourceIp()
    {
        if (SelectedEvent?.SourceAddress != null)
            FilterSourceIp = AppendNegation(FilterSourceIp, SelectedEvent.SourceAddress.ToString());
    }

    [RelayCommand]
    private void ExcludeDestIp()
    {
        if (SelectedEvent?.DestinationAddress != null)
            FilterDestIp = AppendNegation(FilterDestIp, SelectedEvent.DestinationAddress.ToString());
    }

    [RelayCommand]
    private void ExcludeSrcPort()
    {
        if (SelectedEvent != null)
            FilterSrcPort = AppendNegation(FilterSrcPort, SelectedEvent.SourcePort.ToString());
    }

    [RelayCommand]
    private void ExcludeDstPort()
    {
        if (SelectedEvent != null)
            FilterDstPort = AppendNegation(FilterDstPort, SelectedEvent.DestinationPort.ToString());
    }

    [RelayCommand]
    private void ExcludeProtocol()
    {
        if (SelectedEvent != null)
            FilterProtocol = AppendNegation(FilterProtocol, SelectedEvent.Protocol.ToString());
    }

    [RelayCommand]
    private void ExcludeProcess()
    {
        if (SelectedEvent?.ProcessName != null)
            FilterProcess = AppendNegation(FilterProcess, SelectedEvent.ProcessName);
    }

    [RelayCommand]
    private void ExcludeNic()
    {
        if (SelectedEvent?.InterfaceName != null)
            FilterNic = AppendNegation(FilterNic, SelectedEvent.InterfaceName);
    }

    [RelayCommand]
    private void ExcludeAction()
    {
        if (SelectedEvent != null)
            FilterAction = AppendNegation(FilterAction, SelectedEvent.Action.ToString());
    }

    private static string AppendNegation(string current, string value)
        => TrafficEventFilter.AppendNegation(current, value);

    [RelayCommand]
    private void ClearFilters()
    {
        FilterSourceIp = string.Empty;
        FilterSrcPort = string.Empty;
        FilterDestIp = string.Empty;
        FilterDstPort = string.Empty;
        FilterProtocol = string.Empty;
        FilterProcess = string.Empty;
        FilterNic = string.Empty;
        FilterAction = string.Empty;
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

    /// <summary>
    /// Writes the rows currently visible — filters applied — to a CSV file.
    /// The capture buffer is memory-only and rolls over, so without this the evidence
    /// behind an investigation is gone as soon as busy traffic evicts it.
    /// </summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var rows = ((System.Collections.IEnumerable)EventsView).Cast<TrafficEvent>().ToList();

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export traffic",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"winfw-traffic-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            // UTF-8 *with* BOM: adapter and process names carry non-ASCII characters
            // (a Swedish install reports "Bluetooth-nätverksanslutning"), and Excel
            // assumes the local codepage without one.
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));

            writer.WriteLine(Csv.Row(
                "Time", "Direction", "Protocol", "Source IP", "Src Port", "Dest IP",
                "Dst Port", "NIC", "NIC Exact", "Process", "PID", "Action", "Reason",
                "Flow", "Profile", "Country", "Hostname"));

            foreach (var e in rows)
            {
                writer.WriteLine(Csv.Row(
                    e.Timestamp, e.Direction, e.Protocol,
                    e.SourceAddress, e.SourcePort, e.DestinationAddress, e.DestinationPort,
                    e.InterfaceName, e.IsInterfaceExact, e.ProcessName, e.ProcessId,
                    e.Action, e.DropReason, e.FlowDescription, e.Profile,
                    e.Country, e.Hostname));
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not write the file.\n\n{ex.Message}",
                "Export traffic", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>The far end of the connection: the destination when we sent it,
    /// the source when we received it.</summary>
    private static System.Net.IPAddress? PeerAddress(TrafficEvent evt)
        => evt.Direction == TrafficDirection.Outbound
            ? evt.DestinationAddress : evt.SourceAddress;

    /// <summary>
    /// Looks up a peer's reverse-DNS name off the UI thread, then back-fills every
    /// buffered event sharing that peer. Best-effort: a failed lookup caches "no name"
    /// so it is not retried on every batch. Safe to call repeatedly — peers already
    /// known or in flight are skipped.
    /// </summary>
    private async Task ResolveHostnameAsync(System.Net.IPAddress address, string key)
    {
        // Guards run before the first await, so on the UI thread — which is what keeps
        // both collections single-threaded and lock-free.
        if (_hostnameByIp.Count >= MaxTrackedHostnames) return;
        if (!_hostnameLookupsInFlight.Add(key)) return;

        string? name = null;
        try
        {
            await _dnsThrottle.WaitAsync().ConfigureAwait(false);
            try
            {
                name = await _geoIpResolver.ReverseDnsAsync(address).ConfigureAwait(false);
            }
            finally
            {
                _dnsThrottle.Release();
            }
        }
        catch
        {
            // Reverse DNS is best-effort; cached as "no name" below.
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _hostnameLookupsInFlight.Remove(key);
            _hostnameByIp[key] = name;

            if (name == null) return;

            // Rows are already on screen, so this relies on TrafficEvent.Hostname
            // raising a change notification.
            foreach (var e in Events)
            {
                if (PeerAddress(e)?.ToString() == key)
                    e.Hostname = name;
            }
        });
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}
