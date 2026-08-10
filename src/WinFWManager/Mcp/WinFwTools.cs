using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;
using WinFWManager.ViewModels;

namespace WinFWManager.Mcp;

/// <summary>
/// MCP tools exposing WinFW Manager's live state, plus the filter/tab controls needed
/// to drive the UI. Deliberately has no firewall-write surface: nothing here can create,
/// modify, or delete a rule, so the worst an automated caller can do is change what is
/// displayed — recoverable with Clear Filters.
/// </summary>
[McpServerToolType]
public sealed class WinFwTools
{
    // Order must match the TabControl in MainWindow: SelectedTabIndex is an index, so a
    // tab inserted there shifts every one after it.
    private static readonly string[] TabNames =
    {
        "Traffic Monitor", "Audited Blocks", "Log Viewer", "Rules Manager",
        "Network Interfaces", "Dashboard"
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly MainViewModel _main;
    private readonly TrafficMonitorViewModel _traffic;
    private readonly DashboardViewModel _dashboard;
    private readonly RulesManagerViewModel _rules;
    private readonly INetworkInterfaceService _nics;

    public WinFwTools(
        MainViewModel main,
        TrafficMonitorViewModel traffic,
        DashboardViewModel dashboard,
        RulesManagerViewModel rules,
        INetworkInterfaceService nics)
    {
        _main = main;
        _traffic = traffic;
        _dashboard = dashboard;
        _rules = rules;
        _nics = nics;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);

    // ---------------------------------------------------------------- read

    [McpServerTool(Name = "get_current_view")]
    [Description("What the user is looking at right now: the active tab, the filters " +
                 "currently applied on it, and a sample of the rows actually visible. " +
                 "Call this first when the user refers to what they can see on screen.")]
    public Task<string> GetCurrentViewAsync(
        [Description("How many visible rows to include (default 20, max 200).")] int sampleRows = 20)
    {
        var take = Math.Clamp(sampleRows, 0, 200);

        return Ui.RunAsync(() =>
        {
            var index = _main.SelectedTabIndex;
            var tab = index >= 0 && index < TabNames.Length ? TabNames[index] : "Unknown";

            object detail = tab switch
            {
                "Traffic Monitor" => new
                {
                    filters = TrafficFilters(),
                    totalEvents = _traffic.Events.Count,
                    visibleEvents = VisibleTrafficEvents().Count,
                    monitoring = _main.IsMonitoring,
                    // The grid auto-scrolls, so the newest rows are the on-screen ones.
                    rows = VisibleTrafficEvents().TakeLast(take).Select(Describe).ToList()
                },
                "Network Interfaces" => new
                {
                    note = "Pseudo-adapters (NDIS filter bindings, WAN miniports) are hidden " +
                           "unless 'Show hidden adapters' is ticked.",
                    adapters = AdapterSnapshot(includeHidden: false)
                },
                "Dashboard" => new
                {
                    filters = DashboardFilters(),
                    stats = DashboardStats(),
                    graph = GraphSummary()
                },
                "Rules Manager" => new
                {
                    search = _rules.SearchText,
                    store = _rules.SelectedStore.ToString(),
                    showHyperVRules = _rules.ShowHyperVRules,
                    ruleCount = _rules.RuleCount,
                    rows = _rules.Rules.Take(take).Select(Describe).ToList()
                },
                _ => new { note = "This tab exposes no structured state." }
            };

            return Json(new { activeTab = tab, activeTabIndex = index, detail });
        });
    }

    [McpServerTool(Name = "get_adapters")]
    [Description("Network adapters. By default returns only adapters Windows itself " +
                 "considers real; pass includeHidden to also get NDIS filter bindings, " +
                 "WAN miniports and tunnel pseudo-interfaces, of which a typical machine " +
                 "has several dozen.")]
    public Task<string> GetAdaptersAsync(bool includeHidden = false)
        => Ui.RunAsync(() => Json(AdapterSnapshot(includeHidden)));

    [McpServerTool(Name = "get_traffic_events")]
    [Description("Captured traffic events — the most recent ones, in display order. " +
                 "By default returns only events passing the filters currently applied " +
                 "in the Traffic Monitor tab.")]
    public Task<string> GetTrafficEventsAsync(
        [Description("Maximum events to return (default 50, max 500).")] int limit = 50,
        [Description("False to ignore the UI filters and return the raw buffer.")] bool applyUiFilter = true)
    {
        var take = Math.Clamp(limit, 1, 500);

        return Ui.RunAsync(() =>
        {
            var source = applyUiFilter
                ? VisibleTrafficEvents()
                : _traffic.Events.ToList();

            return Json(new
            {
                monitoring = _main.IsMonitoring,
                filtersApplied = applyUiFilter,
                filters = applyUiFilter ? TrafficFilters() : null,
                totalInBuffer = _traffic.Events.Count,
                matched = source.Count,
                returned = Math.Min(take, source.Count),
                // Newest, not oldest: the grid auto-scrolls, so these are the rows the
                // user is actually looking at. Kept in display order within the slice.
                events = source.TakeLast(take).Select(Describe).ToList()
            });
        });
    }

    [McpServerTool(Name = "get_dashboard")]
    [Description("Dashboard state: allow/block and inbound/outbound stats, top talkers, " +
                 "and a summary of the traffic graph topology.")]
    public Task<string> GetDashboardAsync()
        => Ui.RunAsync(() => Json(new
        {
            filters = DashboardFilters(),
            stats = DashboardStats(),
            graph = GraphSummary(),
            // Rankings are sorted by this; "Conversations" ranks by distinct flows,
            // "Packets" by captured volume.
            ranking = _dashboard.Ranking.ToString(),
            topTalkers = _dashboard.TopTalkers
                .Select(t => new { t.Address, t.Hostname, t.Country, t.FlowCount, t.Count }).ToList(),
            topBlocked = _dashboard.TopBlocked
                .Select(t => new { t.Address, t.Hostname, t.Country, t.FlowCount, t.Count }).ToList()
        }));

    [McpServerTool(Name = "get_rules")]
    [Description("Windows Firewall rules as loaded in the Rules Manager tab. " +
                 "Read-only — this server cannot create, change or delete rules.")]
    public Task<string> GetRulesAsync(
        [Description("Case-insensitive substring matched against rule name, group and program.")]
        string? search = null,
        [Description("Maximum rules to return (default 50, max 500).")] int limit = 50)
    {
        var take = Math.Clamp(limit, 1, 500);

        return Ui.RunAsync(() =>
        {
            IEnumerable<FirewallRuleInfo> rules = _rules.Rules;

            if (!string.IsNullOrWhiteSpace(search))
                rules = rules.Where(r =>
                    Contains(r.DisplayName, search) || Contains(r.Name, search) ||
                    Contains(r.Group, search) || Contains(r.Program, search));

            var list = rules.ToList();
            return Json(new
            {
                loadedInUi = _rules.Rules.Count,
                matched = list.Count,
                returned = Math.Min(take, list.Count),
                rules = list.Take(take).Select(Describe).ToList()
            });
        });
    }

    [McpServerTool(Name = "explain_blocks")]
    [Description("For recent dropped events, works out which firewall rule most likely " +
                 "caused each drop by matching the packet against the configured rules. " +
                 "Best-effort: the authoritative filter id is not available without " +
                 "Security auditing enabled, so each result carries a 'conclusive' flag.")]
    public Task<string> ExplainBlocksAsync(
        [Description("How many recent dropped events to explain (default 10, max 50).")] int limit = 10)
    {
        var take = Math.Clamp(limit, 1, 50);

        return Ui.RunAsync(() =>
        {
            var rules = _rules.Rules.ToList();
            if (rules.Count == 0)
                return Json(new { error = "No rules loaded — open the Rules Manager tab first." });

            var drops = _traffic.Events
                .Where(e => e.Action is TrafficAction.Block or TrafficAction.Drop)
                .TakeLast(take)
                .ToList();

            return Json(new
            {
                rulesCompared = rules.Count,
                dropsExamined = drops.Count,
                results = drops.Select(e =>
                {
                    var r = FirewallRuleMatcher.Explain(e, rules);
                    return new
                    {
                        time = e.Timestamp.ToString("HH:mm:ss.fff"),
                        packet = $"{e.Protocol} {e.SourceAddress}:{e.SourcePort} -> " +
                                 $"{e.DestinationAddress}:{e.DestinationPort}",
                        direction = e.Direction.ToString(),
                        process = e.ProcessName,
                        stackReason = e.DropReason,
                        r.Summary,
                        conclusive = r.IsConclusive,
                        blockingRules = r.BlockingRules.Take(3).Select(x => x.DisplayName).ToList(),
                    };
                }).ToList()
            });
        });
    }

    [McpServerTool(Name = "get_audit_blocks")]
    [Description("Blocked packets and connections as recorded by Windows Security " +
                 "auditing (events 5152/5157). Unlike captured traffic these carry the " +
                 "id of the WFP filter that made the decision. Returns the audit policy " +
                 "state too; with auditing off there is nothing to read. Read-only — " +
                 "this cannot change the audit policy, which is done from the app.")]
    public Task<string> GetAuditBlocksAsync(
        [Description("How many recent events to return (default 25, max 200).")] int limit = 25)
    {
        var take = Math.Clamp(limit, 1, 200);

        // Reading the Security log is slow and touches no UI state, so it stays off the
        // dispatcher unlike the other tools here.
        return Task.Run(() =>
        {
            var state = WfpAuditPolicy.GetState();
            var blocks = WfpAuditEventReader.ReadRecent(take);

            return Json(new
            {
                auditPolicy = state.ToString(),
                note = state == WfpAuditState.FailureAudit
                    ? "Auditing is on; blocks are being recorded."
                    : "Auditing is off or unreadable, so events may be absent or stale.",
                returned = blocks.Count,
                blocks = blocks.Select(b => new
                {
                    time = b.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                    b.EventId,
                    b.FilterId,
                    // The whole point of auditing: the filter's name, which for filters
                    // created from firewall rules is the rule's own name.
                    filterName = WfpFilterResolver.Resolve(b.FilterId),
                    layer = WfpAuditEventReader.Humanize(b.LayerName),
                    b.Application,
                    direction = WfpAuditEventReader.Humanize(b.Direction),
                    protocol = WfpAuditEventReader.ProtocolName(b.Protocol),
                    source = $"{b.SourceAddress}:{b.SourcePort}",
                    destination = $"{b.DestAddress}:{b.DestPort}",
                    b.ProcessId,
                }).ToList()
            });
        });
    }

    // ------------------------------------------------------------ ui control

    [McpServerTool(Name = "set_traffic_filter")]
    [Description("Sets filters on the Traffic Monitor tab, changing what the user sees. " +
                 "Only supplied fields change; pass an empty string to clear one. Prefix a " +
                 "value with '!' to exclude instead of include. Returns the resulting counts.")]
    public Task<string> SetTrafficFilterAsync(
        string? sourceIp = null, string? sourcePort = null,
        string? destIp = null, string? destPort = null,
        string? protocol = null, string? process = null,
        string? nic = null,
        [Description("Allow, Block or Drop.")] string? action = null)
        => Ui.RunAsync(() =>
        {
            if (sourceIp != null) _traffic.FilterSourceIp = sourceIp;
            if (sourcePort != null) _traffic.FilterSrcPort = sourcePort;
            if (destIp != null) _traffic.FilterDestIp = destIp;
            if (destPort != null) _traffic.FilterDstPort = destPort;
            if (protocol != null) _traffic.FilterProtocol = protocol;
            if (process != null) _traffic.FilterProcess = process;
            if (nic != null) _traffic.FilterNic = nic;
            if (action != null) _traffic.FilterAction = action;

            return Json(new
            {
                applied = TrafficFilters(),
                totalInBuffer = _traffic.Events.Count,
                visibleEvents = VisibleTrafficEvents().Count
            });
        });

    [McpServerTool(Name = "clear_traffic_filters")]
    [Description("Clears every filter on the Traffic Monitor tab.")]
    public Task<string> ClearTrafficFiltersAsync()
        => Ui.RunAsync(() =>
        {
            _traffic.FilterSourceIp = "";
            _traffic.FilterSrcPort = "";
            _traffic.FilterDestIp = "";
            _traffic.FilterDstPort = "";
            _traffic.FilterProtocol = "";
            _traffic.FilterProcess = "";
            _traffic.FilterNic = "";
            _traffic.FilterAction = "";

            return Json(new
            {
                cleared = true,
                visibleEvents = VisibleTrafficEvents().Count
            });
        });

    [McpServerTool(Name = "select_tab")]
    [Description("Switches the active tab so the user is looking at the relevant view. " +
                 "One of: Traffic Monitor, Log Viewer, Rules Manager, Network Interfaces, Dashboard.")]
    public Task<string> SelectTabAsync(string tab)
        => Ui.RunAsync(() =>
        {
            var index = Array.FindIndex(TabNames,
                t => string.Equals(t, tab, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                return Json(new { error = $"Unknown tab '{tab}'.", validTabs = TabNames });

            _main.SelectedTabIndex = index;
            return Json(new { activeTab = TabNames[index], activeTabIndex = index });
        });

    // ------------------------------------------------------------- helpers

    private static bool Contains(string? haystack, string needle)
        => haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Events passing the Traffic Monitor's filters, in display order.</summary>
    private List<TrafficEvent> VisibleTrafficEvents()
        => ((IEnumerable)_traffic.EventsView).Cast<TrafficEvent>().ToList();

    private object TrafficFilters() => new
    {
        sourceIp = _traffic.FilterSourceIp,
        sourcePort = _traffic.FilterSrcPort,
        destIp = _traffic.FilterDestIp,
        destPort = _traffic.FilterDstPort,
        protocol = _traffic.FilterProtocol,
        process = _traffic.FilterProcess,
        nic = _traffic.FilterNic,
        action = _traffic.FilterAction
    };

    private object DashboardFilters() => new
    {
        sourceIp = _dashboard.FilterSourceIp,
        sourcePort = _dashboard.FilterSrcPort,
        destIp = _dashboard.FilterDestIp,
        destPort = _dashboard.FilterDstPort,
        protocol = _dashboard.FilterProtocol,
        process = _dashboard.FilterProcess,
        nic = _dashboard.FilterNic,
        action = _dashboard.FilterAction,
        drill = _dashboard.HasDrill ? _dashboard.DrillLabel : null
    };

    private object DashboardStats() => new
    {
        total = _dashboard.TotalEvents,
        allowed = _dashboard.AllowedEvents,
        blocked = _dashboard.BlockedEvents,
        blockedPercent = Math.Round(_dashboard.BlockedPercent, 1),
        inbound = _dashboard.InboundCount,
        outbound = _dashboard.OutboundCount
    };

    private object GraphSummary()
    {
        var g = _dashboard.GraphData;
        if (g == null)
            return new { available = false };

        return new
        {
            available = true,
            nodeCount = g.Nodes.Count,
            edgeCount = g.Edges.Count,
            nodes = g.Nodes.Select(n => new
            {
                n.Id,
                n.Label,
                kind = n.Kind.ToString(),
                n.IsLocal,
                n.IsWslGuest,
                n.Country,
                n.Hostname,
                n.ConnectionCount
            }).ToList(),
            edges = g.Edges.Select(e => new
            {
                e.SourceId,
                e.TargetId,
                e.AllowedCount,
                e.BlockedCount,
                dropReasons = e.DropReasons,
                topPorts = e.TopPorts.Select(p => new
                {
                    p.Port, p.Protocol, p.Count, p.BlockedCount
                }).ToList()
            }).ToList()
        };
    }

    private object AdapterSnapshot(bool includeHidden)
    {
        var all = _nics.GetAllAdaptersAsync().GetAwaiter().GetResult();
        var shown = includeHidden ? all : all.Where(a => !a.IsHidden).ToList();

        return new
        {
            total = all.Count,
            hidden = all.Count(a => a.IsHidden),
            returned = shown.Count,
            adapters = shown.Select(a => new
            {
                a.Name,
                description = a.InterfaceAlias,
                type = a.AdapterType.ToString(),
                a.Status,
                a.IsHidden,
                a.MacAddress,
                a.InterfaceIndex,
                ipAddresses = a.IpAddresses.Select(ip => ip.ToString()).ToList()
            }).ToList()
        };
    }

    private static object Describe(TrafficEvent e) => new
    {
        time = e.Timestamp.ToString("HH:mm:ss.fff"),
        direction = e.Direction.ToString(),
        protocol = e.Protocol.ToString(),
        source = e.SourceAddress?.ToString(),
        sourcePort = e.SourcePort,
        destination = e.DestinationAddress?.ToString(),
        destinationPort = e.DestinationPort,
        action = e.Action.ToString(),
        dropReason = e.DropReason,
        process = e.ProcessName,
        pid = e.ProcessId,
        nic = e.InterfaceName,
        nicExact = e.IsInterfaceExact,
        adapterType = e.AdapterType.ToString(),
        country = e.Country,
        hostname = e.Hostname
    };

    private static object Describe(FirewallRuleInfo r) => new
    {
        r.DisplayName,
        r.Enabled,
        direction = r.Direction.ToString(),
        action = r.Action.ToString(),
        protocol = r.Protocol.ToString(),
        r.LocalPort,
        r.RemotePort,
        r.LocalAddress,
        r.RemoteAddress,
        profile = r.Profile.ToString(),
        r.Program,
        r.Group,
        r.IsHyperVRule
    };
}
