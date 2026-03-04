using System.Management.Automation;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class FirewallRuleService : IFirewallRuleService
{
    private readonly PowerShellRunspacePool _pool;
    private bool _hyperVAvailable;

    public bool IsHyperVFirewallAvailable => _hyperVAvailable;

    public FirewallRuleService()
    {
        _pool = new PowerShellRunspacePool();
        CheckHyperVAvailability();
    }

    public async Task<IReadOnlyList<FirewallRuleInfo>> GetRulesAsync(FirewallStore store)
    {
        var storeName = store switch
        {
            FirewallStore.ActiveStore => "ActiveStore",
            FirewallStore.PersistentStore => "PersistentStore",
            FirewallStore.ConfigurableServiceStore => "ConfigurableServiceStore",
            FirewallStore.GPO => "RSOP",
            _ => "ActiveStore"
        };

        var script = $@"
            Get-NetFirewallRule -PolicyStore {storeName} -ErrorAction SilentlyContinue | ForEach-Object {{
                $rule = $_
                $port = $_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
                $addr = $_ | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue
                $app  = $_ | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue
                [PSCustomObject]@{{
                    Name          = $rule.Name
                    DisplayName   = $rule.DisplayName
                    Description   = $rule.Description
                    Enabled       = $rule.Enabled.ToString()
                    Direction     = $rule.Direction.ToString()
                    Action        = $rule.Action.ToString()
                    Profile       = $rule.Profile.ToString()
                    Protocol      = $port.Protocol
                    LocalPort     = $port.LocalPort -join ','
                    RemotePort    = $port.RemotePort -join ','
                    LocalAddress  = $addr.LocalAddress -join ','
                    RemoteAddress = $addr.RemoteAddress -join ','
                    Program       = $app.Program
                    Group         = $rule.Group
                }}
            }}
        ";

        var results = await _pool.InvokeAsync<PSObject>(script);
        return results.Select(r => MapToRuleInfo(r, store)).ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<FirewallRuleInfo>> GetHyperVRulesAsync()
    {
        if (!_hyperVAvailable)
            return Array.Empty<FirewallRuleInfo>().AsReadOnly();

        var script = @"
            Get-NetFirewallHyperVRule -ErrorAction SilentlyContinue | ForEach-Object {
                [PSCustomObject]@{
                    Name        = $_.Name
                    DisplayName = $_.DisplayName
                    Description = $_.Description
                    Enabled     = $_.Enabled.ToString()
                    Direction   = $_.Direction.ToString()
                    Action      = $_.Action.ToString()
                    Profile     = $_.Profile.ToString()
                    Protocol    = $_.Protocol
                    LocalPort   = $_.LocalPorts -join ','
                    RemotePort  = $_.RemotePorts -join ','
                    LocalAddress  = $_.LocalAddresses -join ','
                    RemoteAddress = $_.RemoteAddresses -join ','
                    Program     = ''
                    Group       = ''
                }
            }
        ";

        var results = await _pool.InvokeAsync<PSObject>(script);
        return results.Select(r =>
        {
            var rule = MapToRuleInfo(r, FirewallStore.PersistentStore);
            rule.IsHyperVRule = true;
            return rule;
        }).ToList().AsReadOnly();
    }

    public async Task CreateRuleAsync(FirewallRuleInfo rule)
    {
        var script = rule.IsHyperVRule
            ? BuildHyperVCreateScript(rule)
            : BuildCreateScript(rule);
        await _pool.InvokeAsync(script);
    }

    public async Task UpdateRuleAsync(FirewallRuleInfo rule)
    {
        var parameters = new Dictionary<string, object> { ["Name"] = rule.Name };
        var setProps = new List<string>();

        if (!string.IsNullOrEmpty(rule.DisplayName)) setProps.Add($"-NewDisplayName '{Escape(rule.DisplayName)}'");
        if (!string.IsNullOrEmpty(rule.Description)) setProps.Add($"-Description '{Escape(rule.Description)}'");

        var script = $"Set-NetFirewallRule -Name $Name {string.Join(' ', setProps)}";
        await _pool.InvokeAsync(script, parameters);
    }

    public async Task DeleteRuleAsync(string ruleName)
    {
        var parameters = new Dictionary<string, object> { ["Name"] = ruleName };
        await _pool.InvokeAsync("Remove-NetFirewallRule -Name $Name", parameters);
    }

    public async Task SetRuleEnabledAsync(string ruleName, bool enabled)
    {
        var cmd = enabled ? "Enable-NetFirewallRule" : "Disable-NetFirewallRule";
        var parameters = new Dictionary<string, object> { ["Name"] = ruleName };
        await _pool.InvokeAsync($"{cmd} -Name $Name", parameters);
    }

    public async Task<IReadOnlyList<FirewallProfile>> GetActiveProfilesAsync()
    {
        var script = @"
            Get-NetFirewallProfile | Where-Object { $_.Enabled -eq 'True' } |
            Select-Object -ExpandProperty Name
        ";
        var results = await _pool.InvokeAsync<string>(script);
        return results.Select(ParseProfile).ToList().AsReadOnly();
    }

    public void Dispose()
    {
        _pool.Dispose();
        GC.SuppressFinalize(this);
    }

    private void CheckHyperVAvailability()
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.AddScript("Get-Command Get-NetFirewallHyperVRule -ErrorAction SilentlyContinue");
            var result = ps.Invoke();
            _hyperVAvailable = result.Count > 0;
        }
        catch { _hyperVAvailable = false; }
    }

    private static string BuildCreateScript(FirewallRuleInfo rule) =>
        $@"New-NetFirewallRule -DisplayName '{Escape(rule.DisplayName)}' " +
        $"-Direction {rule.Direction} -Action {rule.Action} " +
        $"-Protocol {rule.Protocol} " +
        (!string.IsNullOrEmpty(rule.LocalPort) ? $"-LocalPort {rule.LocalPort} " : "") +
        (!string.IsNullOrEmpty(rule.RemotePort) ? $"-RemotePort {rule.RemotePort} " : "") +
        (!string.IsNullOrEmpty(rule.LocalAddress) ? $"-LocalAddress {rule.LocalAddress} " : "") +
        (!string.IsNullOrEmpty(rule.RemoteAddress) ? $"-RemoteAddress {rule.RemoteAddress} " : "") +
        (!string.IsNullOrEmpty(rule.Program) ? $"-Program '{Escape(rule.Program)}' " : "") +
        $"-Profile {rule.Profile} -Enabled {(rule.Enabled ? "True" : "False")}";

    private static string BuildHyperVCreateScript(FirewallRuleInfo rule) =>
        $@"New-NetFirewallHyperVRule -DisplayName '{Escape(rule.DisplayName)}' " +
        $"-Direction {rule.Direction} -Action {rule.Action}";

    private static string Escape(string s) => s.Replace("'", "''");

    private static FirewallRuleInfo MapToRuleInfo(PSObject obj, FirewallStore store) => new()
    {
        Name = GetProp(obj, "Name"),
        DisplayName = GetProp(obj, "DisplayName"),
        Description = GetProp(obj, "Description"),
        Enabled = GetProp(obj, "Enabled").Equals("True", StringComparison.OrdinalIgnoreCase),
        Direction = GetProp(obj, "Direction").Contains("Inbound", StringComparison.OrdinalIgnoreCase)
            ? TrafficDirection.Inbound : TrafficDirection.Outbound,
        Action = GetProp(obj, "Action").Contains("Allow", StringComparison.OrdinalIgnoreCase)
            ? TrafficAction.Allow : TrafficAction.Block,
        Protocol = ParseProtocol(GetProp(obj, "Protocol")),
        LocalPort = GetProp(obj, "LocalPort"),
        RemotePort = GetProp(obj, "RemotePort"),
        LocalAddress = GetProp(obj, "LocalAddress"),
        RemoteAddress = GetProp(obj, "RemoteAddress"),
        Program = GetProp(obj, "Program"),
        Group = GetProp(obj, "Group"),
        Profile = ParseProfile(GetProp(obj, "Profile")),
        Store = store
    };

    private static string GetProp(PSObject obj, string name) =>
        obj.Properties[name]?.Value?.ToString() ?? string.Empty;

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
}
