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
}
