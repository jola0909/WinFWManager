using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Queries Windows Firewall rules via native CIM (no PowerShell).
/// Runs 4 parallel WQL queries and joins by InstanceID.
/// </summary>
public sealed class CimFirewallQueryService : IDisposable
{
    private const string CimNamespace = @"root\standardcimv2";
    private readonly CimSession _session;

    public CimFirewallQueryService()
    {
        // Use DCOM protocol for local WMI access (doesn't require WinRM)
        var options = new DComSessionOptions();
        _session = CimSession.Create("localhost", options);
    }

    public async Task<IReadOnlyList<FirewallRuleInfo>> GetRulesAsync(FirewallStore store)
    {
        var rulesTask = Task.Run(() => QueryRules());
        var portsTask = Task.Run(() => QueryInstances("SELECT InstanceID, Protocol, LocalPort, RemotePort FROM MSFT_NetProtocolPortFilter"));
        var addrsTask = Task.Run(() => QueryInstances("SELECT InstanceID, LocalAddress, RemoteAddress FROM MSFT_NetAddressFilter"));
        var appsTask = Task.Run(() => QueryInstances("SELECT InstanceID, AppPath FROM MSFT_NetApplicationFilter"));

        await Task.WhenAll(rulesTask, portsTask, addrsTask, appsTask);

        var rawRules = rulesTask.Result;
        var portLookup = portsTask.Result;
        var addrLookup = addrsTask.Result;
        var appLookup = appsTask.Result;

        var results = new List<FirewallRuleInfo>(rawRules.Count);
        foreach (var (id, rule) in rawRules)
        {
            portLookup.TryGetValue(id, out var port);
            addrLookup.TryGetValue(id, out var addr);
            appLookup.TryGetValue(id, out var app);

            rule.Protocol = ParseProtocol(GetStr(port, "Protocol"));
            rule.LocalPort = JoinArray(port, "LocalPort");
            rule.RemotePort = JoinArray(port, "RemotePort");
            rule.LocalAddress = JoinArray(addr, "LocalAddress");
            rule.RemoteAddress = JoinArray(addr, "RemoteAddress");
            rule.Program = GetStr(app, "AppPath");
            rule.Store = store;

            results.Add(rule);
        }

        return results.AsReadOnly();
    }

    public Task<IReadOnlyList<FirewallProfile>> GetActiveProfilesAsync()
    {
        return Task.Run(() =>
        {
            var profiles = new List<FirewallProfile>();
            foreach (var instance in _session.QueryInstances(CimNamespace, "WQL",
                "SELECT Name, Enabled FROM MSFT_NetFirewallProfile WHERE Enabled = 1"))
            {
                var name = instance.CimInstanceProperties["Name"]?.Value?.ToString() ?? "";
                profiles.Add(ParseProfile(name));
            }
            return (IReadOnlyList<FirewallProfile>)profiles.AsReadOnly();
        });
    }

    public bool CheckHyperVAvailability()
    {
        try
        {
            var results = _session.QueryInstances(CimNamespace, "WQL",
                "SELECT InstanceID FROM MSFT_NetFirewallHyperVRule");
            foreach (var _ in results) break;
            return true;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<FirewallRuleInfo>> GetHyperVRulesAsync()
    {
        return await Task.Run(() =>
        {
            var rules = new List<FirewallRuleInfo>();
            foreach (var instance in _session.QueryInstances(CimNamespace, "WQL",
                "SELECT * FROM MSFT_NetFirewallHyperVRule"))
            {
                rules.Add(new FirewallRuleInfo
                {
                    Name = GetStr(instance, "InstanceID"),
                    DisplayName = GetStr(instance, "DisplayName"),
                    Description = GetStr(instance, "Description"),
                    Enabled = GetUInt16(instance, "Enabled") == 1,
                    Direction = GetUInt16(instance, "Direction") == 1
                        ? TrafficDirection.Inbound : TrafficDirection.Outbound,
                    Action = GetUInt16(instance, "Action") == 2
                        ? TrafficAction.Allow : TrafficAction.Block,
                    Profile = ParseProfileFlags(GetUInt16(instance, "Profiles")),
                    IsHyperVRule = true,
                    Store = FirewallStore.PersistentStore
                });
            }
            return (IReadOnlyList<FirewallRuleInfo>)rules.AsReadOnly();
        });
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    private List<(string Id, FirewallRuleInfo Rule)> QueryRules()
    {
        var rules = new List<(string, FirewallRuleInfo)>();
        foreach (var instance in _session.QueryInstances(CimNamespace, "WQL",
            "SELECT InstanceID, ElementName, DisplayName, Description, Enabled, Direction, Action, Profiles, RuleGroup FROM MSFT_NetFirewallRule"))
        {
            var id = GetStr(instance, "InstanceID");
            rules.Add((id, new FirewallRuleInfo
            {
                Name = id,
                DisplayName = GetStr(instance, "DisplayName"),
                Description = GetStr(instance, "Description"),
                Enabled = GetUInt16(instance, "Enabled") == 1,
                Direction = GetUInt16(instance, "Direction") == 1
                    ? TrafficDirection.Inbound : TrafficDirection.Outbound,
                Action = GetUInt16(instance, "Action") == 2
                    ? TrafficAction.Allow : TrafficAction.Block,
                Profile = ParseProfileFlags(GetUInt16(instance, "Profiles")),
                Group = GetStr(instance, "RuleGroup"),
            }));
        }
        return rules;
    }

    private Dictionary<string, CimInstance> QueryInstances(string query)
    {
        var lookup = new Dictionary<string, CimInstance>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in _session.QueryInstances(CimNamespace, "WQL", query))
        {
            var id = GetStr(instance, "InstanceID");
            if (!string.IsNullOrEmpty(id))
                lookup[id] = instance;
        }
        return lookup;
    }

    private static string GetStr(CimInstance? instance, string name)
    {
        if (instance == null) return "";
        return instance.CimInstanceProperties[name]?.Value?.ToString() ?? "";
    }

    private static ushort GetUInt16(CimInstance instance, string name)
    {
        var val = instance.CimInstanceProperties[name]?.Value;
        return val is ushort u ? u : (ushort)0;
    }

    private static string JoinArray(CimInstance? instance, string name)
    {
        if (instance == null) return "";
        var val = instance.CimInstanceProperties[name]?.Value;
        if (val is string[] arr) return string.Join(",", arr);
        if (val is string s) return s;
        return val?.ToString() ?? "";
    }

    private static TransportProtocol ParseProtocol(string p) => p.ToUpperInvariant() switch
    {
        "TCP" => TransportProtocol.TCP,
        "UDP" => TransportProtocol.UDP,
        "ICMPV4" or "ICMP" => TransportProtocol.ICMP,
        "ICMPV6" => TransportProtocol.ICMPv6,
        _ => TransportProtocol.Other
    };

    private static FirewallProfile ParseProfile(string p) => p.ToLowerInvariant() switch
    {
        "domain" => FirewallProfile.Domain,
        "private" => FirewallProfile.Private,
        "public" => FirewallProfile.Public,
        _ => FirewallProfile.Any
    };

    private static FirewallProfile ParseProfileFlags(ushort flags) => flags switch
    {
        0 => FirewallProfile.Any,
        1 => FirewallProfile.Domain,
        2 => FirewallProfile.Private,
        4 => FirewallProfile.Public,
        _ => FirewallProfile.Any
    };
}
