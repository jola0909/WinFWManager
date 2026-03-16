# Native CIM Rules Loading Design

**Date:** 2026-03-16
**Goal:** Replace sluggish PowerShell subprocess-based firewall rule loading with native CIM queries for fast initial load.

## Problem

The current implementation spawns a `powershell.exe` subprocess for every operation. The read path is slow (~3-5s for ~500 rules) due to:
1. Process startup overhead (~100-200ms per invocation)
2. Per-rule filter piping: each rule individually queries `Get-NetFirewallPortFilter`, `Get-NetFirewallAddressFilter`, and `Get-NetFirewallApplicationFilter` — O(N) CIM calls

## Solution: Native CIM via `Microsoft.Management.Infrastructure`

Use the built-in Windows CIM API (`CimSession`) to query firewall rules directly from C#. No PowerShell for reads.

### Read Path (fast — new)

```
CimSession.Create("localhost")
    ├── Task 1: SELECT * FROM MSFT_NetFirewallRule
    ├── Task 2: SELECT * FROM MSFT_NetFirewallPortFilter
    ├── Task 3: SELECT * FROM MSFT_NetFirewallAddressFilter
    └── Task 4: SELECT * FROM MSFT_NetFirewallApplicationFilter
         ↓  (parallel via Task.WhenAll)
    Join by InstanceID in C#
         ↓
    List<FirewallRuleInfo>
```

- **CIM namespace:** `root/standardcimv2`
- **4 bulk queries** run in parallel — total time = slowest query, not sum
- **Join key:** `InstanceID` on each CIM class links rules to their filters
- **CimSession** created once and reused (singleton, lightweight local WMI connection)
- **No NuGet packages needed** — `Microsoft.Management.Infrastructure` is a built-in Windows assembly (add as framework reference)

### Write Path (keep as-is — unchanged)

`powershell.exe` subprocess for create/update/delete/enable-disable. These are infrequent user-initiated actions where ~200ms overhead is acceptable.

### CIM Property Mapping

**MSFT_NetFirewallRule** (uint16 enums, not strings):
| CIM Property | Type | Values | Maps to |
|---|---|---|---|
| InstanceID | string | Rule identifier | `FirewallRuleInfo.Name` |
| DisplayName | string | | `DisplayName` |
| Description | string | | `Description` |
| Enabled | uint16 | 1=Enabled, 2=Disabled | `Enabled` |
| Direction | uint16 | 1=Inbound, 2=Outbound | `Direction` |
| Action | uint16 | 2=Allow, 3=AllowBypass, 4=Block | `Action` |
| Profiles | uint16 | 0=Any, 1=Domain, 2=Private, 4=Public (flags) | `Profile` |
| RuleGroup | string | | `Group` |

**MSFT_NetFirewallPortFilter:**
| CIM Property | Maps to |
|---|---|
| Protocol | `Protocol` |
| LocalPort | `LocalPort` |
| RemotePort | `RemotePort` |

**MSFT_NetFirewallAddressFilter:**
| CIM Property | Maps to |
|---|---|
| LocalAddress | `LocalAddress` |
| RemoteAddress | `RemoteAddress` |

**MSFT_NetFirewallApplicationFilter:**
| CIM Property | Maps to |
|---|---|
| Program | `Program` |

### What Changes

| File | Change |
|---|---|
| `FirewallRuleService.cs` | Replace `GetRulesAsync` / `GetHyperVRulesAsync` / `GetActiveProfilesAsync` with CIM queries. Keep PS subprocess for mutations. |
| `PowerShellRunspacePool.cs` | No change — still used for mutations |
| `WinFWManager.Core.csproj` | Add `<FrameworkReference Include="Microsoft.Management.Infrastructure" />` or reference the DLL. Remove `System.Management.Automation` NuGet if no longer needed. |
| `IFirewallRuleService` | No change |
| `RulesManagerViewModel` | No change |
| Views/XAML | No change |

### What Doesn't Change

- `IFirewallRuleService` interface
- `RulesManagerViewModel` and all view logic
- All XAML/views
- DI registration
- Mutation operations (create/update/delete/enable-disable)

### Expected Performance

| Metric | Current (PS subprocess) | After (native CIM) |
|---|---|---|
| Initial load ~500 rules | ~3-5s | ~200-500ms |
| Process spawns for read | 1 per refresh | 0 |
| Memory overhead | ~50MB per PS process | Negligible |
| Refresh after edit | ~3-5s (full reload via PS) | ~200-500ms |

### Risk Mitigation

- **CIM filter join key:** If `InstanceID` doesn't directly match between rules and filters, fall back to CIM association queries (`MSFT_NetFirewallRuleFilterByPort` etc.)
- **CIM session errors:** Wrap in try/catch, set `ErrorMessage` on failure
- **PolicyStore filtering:** WQL `WHERE` clause or post-filter in C# if CIM doesn't support PolicyStore parameter directly
