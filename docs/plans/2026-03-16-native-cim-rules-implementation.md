# Native CIM Rules Loading Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the slow PowerShell subprocess-based rule fetching with native CIM queries for ~10x faster firewall rule loading.

**Architecture:** Use `Microsoft.Management.Infrastructure.CimSession` to run 4 parallel WQL queries against `root/standardcimv2` (rules + 3 filter classes), join by `InstanceID` in C#. Keep the `powershell.exe` subprocess for write operations only.

**Tech Stack:** `Microsoft.Management.Infrastructure` (built-in Windows assembly), xunit, FluentAssertions, Moq

---

### Task 1: Add CIM Framework Reference to Core Project

**Files:**
- Modify: `src/WinFWManager.Core/WinFWManager.Core.csproj`

**Step 1: Add the framework reference and remove unused PS SDK**

In `src/WinFWManager.Core/WinFWManager.Core.csproj`, add a `FrameworkReference` for `Microsoft.Management.Infrastructure` and remove the `System.Management.Automation` NuGet package (no longer used by any source file):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MaxMind.GeoIP2" Version="5.4.1" />
    <PackageReference Include="Microsoft.Diagnostics.Tracing.TraceEvent" Version="3.1.30" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.3" />
    <PackageReference Include="System.Reactive" Version="6.1.0" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="Microsoft.Management.Infrastructure">
      <HintPath>$(SystemRoot)\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll</HintPath>
    </Reference>
  </ItemGroup>

</Project>
```

> **Note:** If the GAC reference doesn't resolve, try `<PackageReference Include="Microsoft.Management.Infrastructure" Version="3.0.0" />` as a NuGet fallback.

**Step 2: Verify it builds**

Run: `dotnet build src/WinFWManager.Core/WinFWManager.Core.csproj`
Expected: Build succeeds with 0 errors

**Step 3: Commit**

```bash
git add src/WinFWManager.Core/WinFWManager.Core.csproj
git commit -m "chore: add CIM framework reference, remove unused PS SDK"
```

---

### Task 2: Create CimFirewallQueryService — The Read Path

This is the core new class. It handles all CIM-based read queries.

**Files:**
- Create: `src/WinFWManager.Core/Services/CimFirewallQueryService.cs`

**Step 1: Create the CIM query service**

```csharp
using Microsoft.Management.Infrastructure;
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
        _session = CimSession.Create("localhost");
    }

    public async Task<IReadOnlyList<FirewallRuleInfo>> GetRulesAsync(FirewallStore store)
    {
        // Run all 4 queries in parallel
        var rulesTask = Task.Run(() => QueryRules());
        var portsTask = Task.Run(() => QueryInstances("SELECT InstanceID, Protocol, LocalPort, RemotePort FROM MSFT_NetFirewallPortFilter"));
        var addrsTask = Task.Run(() => QueryInstances("SELECT InstanceID, LocalAddress, RemoteAddress FROM MSFT_NetFirewallAddressFilter"));
        var appsTask = Task.Run(() => QueryInstances("SELECT InstanceID, Program FROM MSFT_NetFirewallApplicationFilter"));

        await Task.WhenAll(rulesTask, portsTask, addrsTask, appsTask);

        var rawRules = rulesTask.Result;
        var portLookup = portsTask.Result;
        var addrLookup = addrsTask.Result;
        var appLookup = appsTask.Result;

        // Join rules with filters by InstanceID
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
            rule.Program = GetStr(app, "Program");
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
            // HyperV firewall rules use a different CIM class
            var results = _session.QueryInstances(CimNamespace, "WQL",
                "SELECT InstanceID FROM MSFT_NetFirewallHyperVRule");
            // Just checking if the query doesn't throw — class exists
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

    // --- Private helpers ---

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

    /// <summary>
    /// CIM Profiles field is a bitmask: 1=Domain, 2=Private, 4=Public, 0=Any.
    /// Map to the single most-specific profile or Any.
    /// </summary>
    private static FirewallProfile ParseProfileFlags(ushort flags) => flags switch
    {
        0 => FirewallProfile.Any,
        1 => FirewallProfile.Domain,
        2 => FirewallProfile.Private,
        4 => FirewallProfile.Public,
        _ => FirewallProfile.Any  // Multiple profiles set = Any for display
    };
}
```

**Step 2: Verify it builds**

Run: `dotnet build src/WinFWManager.Core/WinFWManager.Core.csproj`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/WinFWManager.Core/Services/CimFirewallQueryService.cs
git commit -m "feat: add CimFirewallQueryService for native CIM rule queries"
```

---

### Task 3: Integrate CIM Queries into FirewallRuleService

Replace the PowerShell-based read methods with calls to `CimFirewallQueryService`. Keep PS subprocess for mutations.

**Files:**
- Modify: `src/WinFWManager.Core/Services/FirewallRuleService.cs`

**Step 1: Rewrite FirewallRuleService to use CIM for reads**

```csharp
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
```

**Step 2: Verify it builds**

Run: `dotnet build src/WinFWManager/WinFWManager.csproj`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/WinFWManager.Core/Services/FirewallRuleService.cs
git commit -m "feat: wire CIM read path into FirewallRuleService, keep PS for writes"
```

---

### Task 4: Validate CIM Join Key and Fix if Needed

The design assumes `InstanceID` links rules to their filters. This may not be exactly right — the filters may use a different ID format or need CIM association queries. This task is a manual validation step.

**Step 1: Run the app and test**

Run: `dotnet run --project src/WinFWManager/WinFWManager.csproj`

Go to Rules Manager tab. Check:
- Rules appear quickly (under 1 second)
- Port, Address, and Program columns are populated (not all blank)
- Rule count matches what `Get-NetFirewallRule | Measure-Object` returns in PowerShell

**Step 2: If filter columns are blank — fix the join key**

The InstanceID format between rules and filters may differ. Open a PowerShell window and compare:

```powershell
# Check rule InstanceIDs
Get-CimInstance -Namespace root/standardcimv2 -ClassName MSFT_NetFirewallRule | Select-Object -First 3 InstanceID

# Check port filter InstanceIDs
Get-CimInstance -Namespace root/standardcimv2 -ClassName MSFT_NetFirewallPortFilter | Select-Object -First 3 InstanceID
```

If the IDs don't match, we need to use CIM association queries instead. Update `CimFirewallQueryService.QueryInstances` to use `EnumerateAssociatedInstances`:

```csharp
// Fallback: use CIM associations instead of InstanceID join
private Dictionary<string, CimInstance> QueryAssociatedFilters(
    IEnumerable<CimInstance> ruleInstances, string filterClass)
{
    var lookup = new Dictionary<string, CimInstance>(StringComparer.OrdinalIgnoreCase);
    foreach (var rule in ruleInstances)
    {
        var id = GetStr(rule, "InstanceID");
        foreach (var assoc in _session.EnumerateAssociatedInstances(
            CimNamespace, rule, filterClass, null, null, null))
        {
            lookup[id] = assoc;
            break; // 1:1 relationship
        }
    }
    return lookup;
}
```

**Step 3: Commit any fixes**

```bash
git add -u
git commit -m "fix: adjust CIM filter join key based on validation"
```

---

### Task 5: Build, Run, and Verify End-to-End

**Step 1: Full build**

Run: `dotnet build src/WinFWManager/WinFWManager.csproj`
Expected: 0 errors, 0 warnings (or only pre-existing warnings)

**Step 2: Run existing tests**

Run: `dotnet test tests/WinFWManager.Tests/WinFWManager.Tests.csproj`
Expected: All existing tests pass (none should be affected since we didn't change interfaces)

**Step 3: Manual smoke test**

Run: `dotnet run --project src/WinFWManager/WinFWManager.csproj`

Verify:
- [ ] Rules Manager tab loads rules in under 1 second
- [ ] Rule count matches PowerShell `(Get-NetFirewallRule).Count`
- [ ] Port, Address, Program columns are populated
- [ ] Profile filter works
- [ ] Search filter works
- [ ] Clicking Refresh reloads rules
- [ ] Changing Store dropdown reloads rules
- [ ] Enable/Disable toggle works (still uses PS subprocess)
- [ ] Create new rule works
- [ ] Delete rule works
- [ ] No errors in the status bar

**Step 4: Final commit**

```bash
git add -A
git commit -m "feat: complete native CIM rules loading — ~10x faster initial load"
```
