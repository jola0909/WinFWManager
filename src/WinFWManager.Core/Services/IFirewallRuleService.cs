using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public interface IFirewallRuleService : IDisposable
{
    Task<IReadOnlyList<FirewallRuleInfo>> GetRulesAsync(FirewallStore store);
    Task<IReadOnlyList<FirewallRuleInfo>> GetHyperVRulesAsync();
    Task CreateRuleAsync(FirewallRuleInfo rule);
    Task UpdateRuleAsync(FirewallRuleInfo rule);
    Task DeleteRuleAsync(string ruleName);
    Task SetRuleEnabledAsync(string ruleName, bool enabled);
    Task<IReadOnlyList<FirewallProfile>> GetActiveProfilesAsync();
    bool IsHyperVFirewallAvailable { get; }
}
