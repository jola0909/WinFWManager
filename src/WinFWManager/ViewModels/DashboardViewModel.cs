using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinFWManager.Core.Collections;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class DashboardViewModel : ObservableObject, IDisposable
{
    private const int BufferCapacity = 10_000;

    private readonly IEtwTrafficMonitor _etwMonitor;
    private readonly INetworkInterfaceService _nicService;
    private readonly IGeoIpResolver _geoIpResolver;
    private readonly RingBuffer<TrafficEvent> _recentEvents = new(BufferCapacity);
    private readonly TrafficEventFilter _filter = new();
    private readonly HashSet<RemoteGroupKind> _expandedGroups = new();

    // Reverse-DNS results keyed by IP. A cached null means "resolved, no name" —
    // distinct from "not looked up yet" (absent key), so a failed lookup is not
    // retried on every 2s rebuild.
    private readonly Dictionary<string, string?> _hostnameByIp = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hostnameLookupsInFlight = new(StringComparer.Ordinal);

    private IDisposable? _subscription;
    private readonly DispatcherTimer _refreshTimer;

    private List<NetworkAdapterInfo> _adapters = new();

    [ObservableProperty] private int _totalEvents;
    [ObservableProperty] private int _blockedEvents;
    [ObservableProperty] private double _blockedPercent;
    [ObservableProperty] private int _allowedEvents;
    [ObservableProperty] private double _allowedPercent;
    [ObservableProperty] private int _inboundCount;
    [ObservableProperty] private int _outboundCount;
    [ObservableProperty] private double _inboundPercent;
    [ObservableProperty] private TrafficGraphData? _graphData;

    [ObservableProperty] private string _filterSourceIp = string.Empty;
    [ObservableProperty] private string _filterSrcPort = string.Empty;
    [ObservableProperty] private string _filterDestIp = string.Empty;
    [ObservableProperty] private string _filterDstPort = string.Empty;
    [ObservableProperty] private string _filterProtocol = string.Empty;
    [ObservableProperty] private string _filterProcess = string.Empty;
    [ObservableProperty] private string _filterNic = string.Empty;
    [ObservableProperty] private string _filterAction = string.Empty;

    /// <summary>
    /// When false (the default) mDNS/LLMNR/SSDP group destinations are left out of the
    /// top-talker rankings. They are real traffic, but they are destinations rather than
    /// peers and otherwise crowd out actual endpoints.
    /// </summary>
    [ObservableProperty] private bool _showMulticastGroups;

    /// <summary>
    /// What the top-talker lists sort by. The two answer different questions: packets
    /// finds whatever is moving the most traffic, conversations finds who this machine
    /// deals with most. A single download wins the first and barely registers in the
    /// second, so neither is right for every purpose.
    /// </summary>
    [ObservableProperty] private TopTalkerRanking _ranking = TopTalkerRanking.Conversations;

    public IReadOnlyList<TopTalkerRanking> RankingOptions { get; } =
        Enum.GetValues<TopTalkerRanking>();

    [ObservableProperty] private DrillSelection? _drill;
    [ObservableProperty] private string _drillLabel = string.Empty;
    [ObservableProperty] private bool _hasDrill;

    public ObservableCollection<TopTalkerEntry> TopTalkers { get; } = new();
    public ObservableCollection<TopTalkerEntry> TopBlocked { get; } = new();

    /// <summary>Capacity of the rolling event buffer feeding the dashboard.</summary>
    public int BufferSize => BufferCapacity;

    public DashboardViewModel(IEtwTrafficMonitor etwMonitor, INetworkInterfaceService nicService,
        IGeoIpResolver geoIpResolver)
    {
        _etwMonitor = etwMonitor;
        _nicService = nicService;
        _geoIpResolver = geoIpResolver;

        _ = InitAdaptersAsync();

        _subscription = _etwMonitor.TrafficEvents
            .Buffer(TimeSpan.FromMilliseconds(500))
            .Where(batch => batch.Count > 0)
            .ObserveOn(System.Threading.SynchronizationContext.Current!)
            .Subscribe(OnEventBatch);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshStats();
        _refreshTimer.Start();
    }

    private async Task InitAdaptersAsync()
    {
        var adapters = await _nicService.GetAllAdaptersAsync();
        _adapters = adapters.ToList();
    }

    private void OnEventBatch(IList<TrafficEvent> batch)
    {
        foreach (var evt in batch)
            _recentEvents.Add(evt);
    }

    partial void OnFilterSourceIpChanged(string value) { _filter.SourceIp = value; RefreshStats(); }
    partial void OnFilterSrcPortChanged(string value) { _filter.SrcPort = value; RefreshStats(); }
    partial void OnFilterDestIpChanged(string value) { _filter.DestIp = value; RefreshStats(); }
    partial void OnFilterDstPortChanged(string value) { _filter.DstPort = value; RefreshStats(); }
    partial void OnFilterProtocolChanged(string value) { _filter.Protocol = value; RefreshStats(); }
    partial void OnFilterProcessChanged(string value) { _filter.Process = value; RefreshStats(); }
    partial void OnFilterNicChanged(string value) { _filter.Nic = value; RefreshStats(); }
    partial void OnFilterActionChanged(string value) { _filter.Action = value; RefreshStats(); }
    partial void OnShowMulticastGroupsChanged(bool value) => RefreshStats();
    partial void OnRankingChanged(TopTalkerRanking value) => RefreshStats();

    partial void OnDrillChanged(DrillSelection? value)
    {
        DrillLabel = value == null ? string.Empty : DescribeDrill(value);
        HasDrill = value != null;
        RefreshStats();
    }

    private static string DescribeDrill(DrillSelection drill) => drill.Kind switch
    {
        GraphNodeKind.RemoteGroup => drill.Value switch
        {
            nameof(RemoteGroupKind.WslGuest) => "WSL guest",
            nameof(RemoteGroupKind.Lan) => "LAN",
            nameof(RemoteGroupKind.Internet) => "Internet",
            _ => drill.Value
        },
        _ => drill.Value
    };

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
    private void ClearDrill() => Drill = null;

    /// <summary>Copies an overview of the currently displayed graph (headline
    /// counts, active filters, top talkers) to the clipboard as plain text.</summary>
    [RelayCommand]
    private void CopyGraphSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinFW Manager — traffic graph summary");
        sb.AppendLine($"Total events      : {TotalEvents:N0}");
        sb.AppendLine($"Allowed           : {AllowedEvents:N0} ({AllowedPercent:F1}%)");
        sb.AppendLine($"Blocked           : {BlockedEvents:N0} ({BlockedPercent:F1}%)");
        sb.AppendLine($"Inbound/Outbound  : {InboundCount:N0} / {OutboundCount:N0}");

        if (HasDrill)
            sb.AppendLine($"Drill             : {DrillLabel}");
        if (HasFilters)
            sb.AppendLine($"Filters           : {DescribeFilters()}");

        if (TopTalkers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Top destinations:");
            foreach (var t in TopTalkers)
                sb.AppendLine($"  {t.Count,8:N0}  {t.Address}  [{t.Country}]");
        }

        if (TopBlocked.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Top blocked:");
            foreach (var t in TopBlocked)
                sb.AppendLine($"  {t.Count,8:N0}  {t.Address}  [{t.Country}]");
        }

        CopyToClipboard(sb.ToString());
    }

    /// <summary>Human-readable rendering of the active filter fields.</summary>
    public string DescribeFilters()
    {
        var parts = new List<string>();
        void Add(string label, string value)
        {
            if (!string.IsNullOrEmpty(value)) parts.Add($"{label}={value}");
        }

        Add("srcIp", FilterSourceIp);
        Add("srcPort", FilterSrcPort);
        Add("dstIp", FilterDestIp);
        Add("dstPort", FilterDstPort);
        Add("proto", FilterProtocol);
        Add("process", FilterProcess);
        Add("nic", FilterNic);
        Add("action", FilterAction);

        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }

    /// <summary>
    /// Clipboard writes fail sporadically when another process holds the
    /// clipboard open; a failed copy should never take the dashboard down.
    /// </summary>
    public static void CopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard busy — nothing useful to do, and not worth a dialog.
        }
    }

    /// <summary>
    /// Node click from the graph view: group nodes expand/collapse, other
    /// nodes set (or replace) the drill selection.
    /// </summary>
    public void ToggleNode(GraphNode node)
    {
        switch (node.Kind)
        {
            case GraphNodeKind.RemoteGroup:
                if (node.Group is not RemoteGroupKind kind) return;
                if (node.Id.StartsWith("more:", StringComparison.Ordinal)
                    || _expandedGroups.Contains(kind))
                {
                    _expandedGroups.Remove(kind);
                }
                else
                {
                    _expandedGroups.Add(kind);
                }
                RefreshStats();
                break;

            case GraphNodeKind.Process:
                if (node.Label == TrafficGraphBuilder.OthersProcessLabel) return;
                Drill = new DrillSelection(GraphNodeKind.Process, node.Label);
                break;

            case GraphNodeKind.Adapter:
                Drill = new DrillSelection(GraphNodeKind.Adapter, node.Label);
                break;

            case GraphNodeKind.Remote:
                var ip = node.Id.StartsWith("ip:", StringComparison.Ordinal)
                    ? node.Id[3..] : node.Label;
                Drill = new DrillSelection(GraphNodeKind.Remote, ip);
                break;
        }
    }

    /// <summary>True when a shared filter or drill selection is narrowing the
    /// events feeding the graph (used by the view for the empty-state text).</summary>
    public bool IsGraphFiltered => !_filter.IsEmpty || HasDrill;

    /// <summary>True when any filter field is set (drill excluded).</summary>
    public bool HasFilters => !_filter.IsEmpty;

    // ---- Context-menu surface (graph right-click) -------------------------

    /// <summary>True when <paramref name="node"/> is the current drill target,
    /// so the menu can show it as checked / offer "clear" instead.</summary>
    public bool IsDrilledTo(GraphNode node)
        => Drill != null && Drill.Kind == node.Kind && Drill.Value == DrillValueOf(node);

    /// <summary>Sets the drill selection to this node without toggling group
    /// expansion (unlike <see cref="ToggleNode"/>, which left-click uses).</summary>
    public void DrillTo(GraphNode node)
    {
        var value = DrillValueOf(node);
        if (value != null)
            Drill = new DrillSelection(node.Kind, value);
    }

    /// <summary>True when the node's remote group is currently expanded.</summary>
    public bool IsGroupExpanded(GraphNode node)
        => node.Group is RemoteGroupKind kind && _expandedGroups.Contains(kind);

    /// <summary>The drill value a node maps to, or null when it is an
    /// aggregate bucket that cannot be drilled ("(others)", "+N more").</summary>
    public static string? DrillValueOf(GraphNode node) => node.Kind switch
    {
        GraphNodeKind.Process => node.Label == TrafficGraphBuilder.OthersProcessLabel ? null : node.Label,
        GraphNodeKind.Adapter => node.Label,
        GraphNodeKind.Remote => RemoteIpOf(node),
        GraphNodeKind.RemoteGroup => node.Id.StartsWith("more:", StringComparison.Ordinal)
            ? null : node.Group?.ToString(),
        _ => null
    };

    /// <summary>The bare IP behind a remote node ("ip:1.2.3.4" → "1.2.3.4").</summary>
    public static string RemoteIpOf(GraphNode node)
        => node.Id.StartsWith("ip:", StringComparison.Ordinal) ? node.Id[3..] : node.Label;

    public void FilterByDestIp(string ip) => FilterDestIp = ip;
    public void FilterBySourceIp(string ip) => FilterSourceIp = ip;
    public void ExcludeDestIp(string ip) => FilterDestIp = TrafficEventFilter.AppendNegation(FilterDestIp, ip);
    public void ExcludeSourceIp(string ip) => FilterSourceIp = TrafficEventFilter.AppendNegation(FilterSourceIp, ip);

    public void FilterByProcessName(string name) => FilterProcess = name;
    public void ExcludeProcessName(string name) => FilterProcess = TrafficEventFilter.AppendNegation(FilterProcess, name);

    public void FilterByNicName(string name) => FilterNic = name;
    public void ExcludeNicName(string name) => FilterNic = TrafficEventFilter.AppendNegation(FilterNic, name);

    /// <summary>Narrows to one destination port + protocol (edge menu).</summary>
    public void FilterByPort(int port, string protocol)
    {
        FilterDstPort = port.ToString();
        FilterProtocol = protocol;
    }

    public void ExcludePort(int port) => FilterDstPort = TrafficEventFilter.AppendNegation(FilterDstPort, port.ToString());

    /// <summary>Cached reverse-DNS name for an IP, or null when unresolved.</summary>
    public string? HostnameFor(string ip)
        => _hostnameByIp.TryGetValue(ip, out var name) ? name : null;

    /// <summary>True once a lookup for this IP has completed (successfully or not).</summary>
    public bool HostnameResolved(string ip) => _hostnameByIp.ContainsKey(ip);

    /// <summary>
    /// Resolves an IP's reverse-DNS name in the background and caches it, then
    /// rebuilds so the graph label and tooltip pick it up. Safe to call
    /// repeatedly: already-resolved and in-flight IPs are skipped.
    /// </summary>
    public async Task ResolveHostnameAsync(string ip)
    {
        if (_hostnameByIp.ContainsKey(ip) || !_hostnameLookupsInFlight.Add(ip))
            return;

        string? name = null;
        try
        {
            if (System.Net.IPAddress.TryParse(ip, out var address))
                name = await _geoIpResolver.ReverseDnsAsync(address);
        }
        catch
        {
            // Reverse DNS is best-effort; a failure caches "no name" below so
            // the lookup is not retried on every rebuild.
        }
        finally
        {
            _hostnameLookupsInFlight.Remove(ip);
        }

        _hostnameByIp[ip] = name;
        RefreshStats();
    }

    private void RefreshStats()
    {
        // COUPLING NOTE: _recentEvents holds the SAME TrafficEvent instances
        // that TrafficMonitorViewModel enriches in place (ProcessName,
        // InterfaceName, AdapterType) on the UI thread. Graph attribution
        // therefore depends on TrafficMonitorViewModel being an eagerly
        // created, never-disposed singleton. If that wiring ever changes,
        // enrichment must move into the monitor pipeline itself.
        IEnumerable<TrafficEvent> query = _recentEvents.ToList();
        if (!_filter.IsEmpty)
            query = query.Where(_filter.Matches);
        if (Drill != null)
            query = query.Where(e => TrafficGraphBuilder.MatchesDrill(e, Drill, _adapters));
        var events = query.ToList();

        TotalEvents = events.Count;
        BlockedEvents = events.Count(e => e.Action is TrafficAction.Block or TrafficAction.Drop);
        AllowedEvents = events.Count(e => e.Action == TrafficAction.Allow);
        BlockedPercent = TotalEvents > 0 ? (double)BlockedEvents / TotalEvents * 100 : 0;
        AllowedPercent = TotalEvents > 0 ? (double)AllowedEvents / TotalEvents * 100 : 0;
        InboundCount = events.Count(e => e.Direction == TrafficDirection.Inbound);
        OutboundCount = events.Count(e => e.Direction == TrafficDirection.Outbound);

        int directional = InboundCount + OutboundCount;
        InboundPercent = directional > 0 ? (double)InboundCount / directional * 100 : 0;

        OnPropertyChanged(nameof(HasFilters));

        // Multicast this machine sends is looped back and captured as inbound, so its
        // own adapter addresses turn up as "peers". Genuine traffic, but it is not
        // someone we are talking to — exclude it so the ranking stays about the network.
        // Keyed without the IPv6 scope: an adapter reports its link-local as
        // "fe80::1%3" while captured events carry it without, so comparing the rendered
        // strings silently misses every IPv6 link-local.
        var localIps = _adapters
            .SelectMany(a => a.IpAddresses)
            .Select(IpAddressUtils.ScopelessKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool IsPeer(TrafficEvent e)
        {
            if (e.RemoteAddress == null) return false;
            if (localIps.Contains(IpAddressUtils.ScopelessKey(e.RemoteAddress))) return false;
            return ShowMulticastGroups || !IpAddressUtils.IsMulticastOrBroadcast(e.RemoteAddress);
        }

        // Top talkers by remote peer
        FillTopTalkers(TopTalkers, events.Where(IsPeer));

        // Top blocked peers
        FillTopTalkers(TopBlocked, events
            .Where(e => e.Action is TrafficAction.Block or TrafficAction.Drop && IsPeer(e)));

        var graph = TrafficGraphBuilder.Build(events, _adapters, _expandedGroups);
        ApplyHostnames(graph);
        GraphData = graph;
    }

    /// <summary>
    /// Groups events by remote peer into the top 5 entries, with each entry's share of
    /// the leader for the inline bars.
    ///
    /// Sorted by whichever metric <see cref="Ranking"/> selects; both are always
    /// carried, since capture is per packet and the two answer different questions.
    /// A single QUIC download was observed filling 9329 of the 10000 buffer entries
    /// from one flow — top by packets, near-invisible by conversations.
    /// </summary>
    private void FillTopTalkers(ObservableCollection<TopTalkerEntry> target,
        IEnumerable<TrafficEvent> events)
    {
        var byPackets = Ranking == TopTalkerRanking.Packets;

        var all = events
            .GroupBy(e => e.RemoteAddress!.ToString())
            .Select(g => new TopTalkerEntry
            {
                Address = g.Key,
                FlowCount = g.Select(e => e.FlowKey).Distinct().Count(),
                Count = g.Count(),
                Country = g.First().Country ?? "Unknown",
                Hostname = HostnameFor(g.Key)
            });

        var entries = (byPackets
                ? all.OrderByDescending(t => t.Count).ThenByDescending(t => t.FlowCount)
                : all.OrderByDescending(t => t.FlowCount).ThenByDescending(t => t.Count))
            .Take(5)
            .ToList();

        // The row leads with whatever is being sorted on, so the numbers always read in
        // descending order, with the other metric alongside for context.
        foreach (var e in entries)
        {
            e.PrimaryValue = byPackets ? e.Count : e.FlowCount;
            e.SecondaryText = byPackets ? $"({e.FlowCount:N0}c)" : $"({e.Count:N0}p)";
            e.PrimaryTooltip = byPackets
                ? "Captured packets"
                : "Distinct conversations with this peer";
        }

        // Resolve names for whatever actually ranked. The monitor resolves peers it
        // sees, but the dashboard keeps its own cache, so without this the list shows
        // bare addresses for hosts whose names are already known elsewhere. Each
        // completed lookup calls RefreshStats, which repopulates these entries.
        foreach (var e in entries)
        {
            if (!HostnameResolved(e.Address))
                _ = ResolveHostnameAsync(e.Address);
        }

        // Share bar tracks the ranking metric, so the bars match the order.
        int max = entries.Count > 0 ? entries[0].PrimaryValue : 0;
        foreach (var e in entries)
            e.SharePercent = max > 0 ? (double)e.PrimaryValue / max * 100 : 0;

        target.Clear();
        foreach (var e in entries)
            target.Add(e);
    }

    /// <summary>Stamps cached reverse-DNS names onto freshly built remote nodes
    /// (the builder is pure and has no access to the cache).</summary>
    private void ApplyHostnames(TrafficGraphData graph)
    {
        if (_hostnameByIp.Count == 0) return;

        foreach (var node in graph.Nodes)
        {
            if (node.Kind != GraphNodeKind.Remote) continue;
            if (_hostnameByIp.TryGetValue(RemoteIpOf(node), out var name))
                node.Hostname = name;
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _subscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>What the top-talker lists sort by.</summary>
public enum TopTalkerRanking
{
    /// <summary>Distinct conversations — who this machine deals with most.</summary>
    Conversations,

    /// <summary>Captured packets — what is moving the most traffic.</summary>
    Packets,
}

public class TopTalkerEntry
{
    public string Address { get; set; } = string.Empty;

    /// <summary>Distinct conversations with this peer.</summary>
    public int FlowCount { get; set; }

    /// <summary>Captured packets, which is volume rather than conversation count.</summary>
    public int Count { get; set; }

    /// <summary>The metric currently being ranked on, shown as the row's headline number.</summary>
    public int PrimaryValue { get; set; }

    /// <summary>The other metric, shown smaller beside it — "(1,234p)" or "(12c)".</summary>
    public string SecondaryText { get; set; } = string.Empty;

    public string PrimaryTooltip { get; set; } = string.Empty;
    public string Country { get; set; } = "Unknown";

    /// <summary>Reverse-DNS name when it has been resolved, else null.</summary>
    public string? Hostname { get; set; }

    /// <summary>This entry's count as a percentage of the list leader, used to
    /// size the inline share bar.</summary>
    public double SharePercent { get; set; }

    /// <summary>Hostname when known, otherwise the bare address.</summary>
    public string DisplayName => string.IsNullOrEmpty(Hostname) ? Address : Hostname;
}
