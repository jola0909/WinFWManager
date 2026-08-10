using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class FirewallRuleService : IFirewallRuleService
{
    private readonly CimFirewallQueryService _cimQuery;
    private readonly PowerShellRunspacePool _pool;
    private readonly bool _hyperVAvailable;

    public bool IsHyperVFirewallAvailable => _hyperVAvailable;

    public FirewallRuleService()
    {
        _cimQuery = new CimFirewallQueryService();
        _pool = new PowerShellRunspacePool();
        _hyperVAvailable = _cimQuery.CheckHyperVAvailability();
    }

    // --- Read operations: native CIM (fast) ---

    public Task<IReadOnlyList<FirewallRuleInfo>> GetRulesAsync(FirewallStore store)
        => _cimQuery.GetRulesAsync(store);

    public Task<IReadOnlyList<FirewallRuleInfo>> GetHyperVRulesAsync()
        => _cimQuery.GetHyperVRulesAsync();

    public Task<IReadOnlyList<FirewallProfile>> GetActiveProfilesAsync()
        => _cimQuery.GetActiveProfilesAsync();

    // --- Write operations: PowerShell subprocess (infrequent) ---

    public async Task CreateRuleAsync(FirewallRuleInfo rule)
    {
        var script = rule.IsHyperVRule
            ? BuildHyperVCreateScript(rule)
            : BuildCreateScript(rule);
        await _pool.InvokeAsync(script);
    }

    /// <summary>
    /// Applies every field the editor exposes. Previously only the display name and
    /// description were sent, so changing a rule's protocol, ports, addresses or program
    /// appeared to work and silently did nothing.
    ///
    /// Cleared fields are written as "Any" rather than skipped, so a condition can be
    /// removed and not just added.
    /// </summary>
    public async Task UpdateRuleAsync(FirewallRuleInfo rule)
    {
        var parameters = new Dictionary<string, object> { ["Name"] = rule.Name };

        var setProps = new List<string>
        {
            $"-Direction {rule.Direction}",
            $"-Action {rule.Action}",
            $"-Enabled {(rule.Enabled ? "True" : "False")}",
            $"-Profile {rule.Profile}",
            $"-Protocol {ProtocolArg(rule.Protocol)}",
            $"-LocalPort {OrAny(rule.LocalPort)}",
            $"-RemotePort {OrAny(rule.RemotePort)}",
            $"-LocalAddress {OrAny(rule.LocalAddress)}",
            $"-RemoteAddress {OrAny(rule.RemoteAddress)}",
            $"-Program {(string.IsNullOrWhiteSpace(rule.Program) ? "Any" : $"'{Escape(rule.Program)}'")}",
        };

        if (!string.IsNullOrEmpty(rule.DisplayName)) setProps.Add($"-NewDisplayName '{Escape(rule.DisplayName)}'");
        if (!string.IsNullOrEmpty(rule.Description)) setProps.Add($"-Description '{Escape(rule.Description)}'");

        var script = $"Set-NetFirewallRule -Name $Name {string.Join(' ', setProps)}";
        await _pool.InvokeAsync(script, parameters);
    }

    /// <summary>Empty means "no restriction", which the cmdlets spell "Any".</summary>
    private static string OrAny(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Any" : value;

    /// <summary>
    /// "Other" is this app's bucket for protocols it does not name, not something the
    /// cmdlets accept, so it maps to no protocol restriction.
    /// </summary>
    private static string ProtocolArg(TransportProtocol protocol)
        => protocol == TransportProtocol.Other ? "Any" : protocol.ToString();

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

    public void Dispose()
    {
        _cimQuery.Dispose();
        _pool.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- PS script builders (unchanged) ---

    private static string BuildCreateScript(FirewallRuleInfo rule) =>
        $@"New-NetFirewallRule -DisplayName '{Escape(rule.DisplayName)}' " +
        $"-Direction {rule.Direction} -Action {rule.Action} " +
        $"-Protocol {ProtocolArg(rule.Protocol)} " +
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
}
