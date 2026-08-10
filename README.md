# WinFW Manager

A modern Windows Firewall management application built with WPF and .NET 10. Monitor real-time network traffic, manage firewall rules, inspect network interfaces, and visualize traffic flow — all from a single dark-themed UI.

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Windows](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

### Traffic Monitor
- **Real-time packet capture** via ETW (Event Tracing for Windows) — no packet sniffing drivers required
- Live table view with time, direction, source/destination IPs and ports, protocol, NIC, process name, action, drop reason, flow, profile, country, and hostname
- **Positive and negative filtering** — filter by any column, or prefix with `!` to exclude unwanted traffic
- **Right-click context menu** with include *and* exclude filters for Source IP, Src Port, Dest IP, Dst Port, Protocol, Process, NIC, and Action, plus copy-selected-rows and clear-all-filters
- **Create a firewall rule straight from an event** — right-click → *Create Rule from Traffic…* pre-fills the rule editor from the selected row
- **WSL2 / Hyper-V awareness** — traffic is attributed to the owning adapter by IP (exact match, then subnet match on either endpoint), so host↔VM flows are tagged to `vEthernet (WSL …)` / Hyper-V switches and colour-coded (WSL = yellow, Hyper-V = blue)
- **Real Allow/Drop actions** — each event shows whether the firewall allowed or dropped it, with the resolved drop reason in its own **Reason** column (e.g. *Filter Block*, *No matching endpoint*) and also as a tooltip on the Action cell
- **Flow column** — a compact traffic path per event, e.g. `WSL guest → vEthernet (WSL) ⛔`
- **Exact adapter attribution for dropped traffic** — dropped packets carry the interface index, so the NIC is identified exactly; *italic* NIC text means the adapter was derived by subnet matching instead

> **WSL traffic visibility.** WSL→host traffic — both allowed **and** firewall-dropped — is captured via the `Microsoft-Windows-TCPIP` manifest provider, with exact adapter attribution and human-readable drop reasons. The remaining limitation is unchanged: in NAT mode, WSL2 guest→internet traffic is NAT-forwarded by WinNAT and never becomes a host socket, so capturing it would require adapter-level capture (`pktmon`/NDIS), which is out of scope for the ETW-based design. WinFW Manager is aware of the WSL networking mode: **NAT** is fully supported; **Mirrored** is detected and an explanatory banner is shown (WSL traffic is indistinguishable from host traffic by design); **Bridged** is handled best-effort via guest IP tagging.

### Dashboard
- At-a-glance stats: captured events, allow/block split, direction, top talkers
  (counts are **packet events**, not connections — a single busy stream can produce
  thousands in a second)
- **Interactive network traffic graph** showing traffic flow between local NICs and remote endpoints
- Hover tooltips with detailed connection info (byte counts, allowed/blocked, top ports, country)
- **WSL-guest nodes highlighted yellow**; fully-blocked flows drawn as dashed red edges, with drop reasons listed in the edge tooltip
- Filter the graph to focus on specific endpoints

### Rules Manager
- View all Windows Firewall rules with search and filtering
- Create, edit, and delete inbound/outbound rules
- Toggle rules on/off

### Log Viewer
- Parse and display Windows Firewall log files
- Filter and search through historical firewall events

### Network Interfaces
- View all network adapters with status, IP addresses, MAC, speed, and type
- **WSL networking-mode badge** showing the detected mode (NAT / Mirrored / Bridged) and the guest IP
- **Pseudo-adapters hidden by default** — .NET reports every NDIS binding (48 on a typical
  machine: WFP/QoS filter bindings, WAN miniports, tunnel interfaces). Only adapters Windows
  itself lists are shown, with a *Show hidden adapters* toggle to see the rest
- Auto-refreshes on first visit

### Audited Blocks
A dedicated tab listing blocks recorded by Windows audit logging, each showing **which
filter or rule** made the decision, with a free-text filter and CSV export.

This is the only place **outbound rule blocks** appear at all: Windows refuses them
before a packet exists, so traffic capture never sees them. Needs block auditing on.

### Why was this blocked?
Right-click any dropped row to ask which rule caused it. There are two levels of answer,
because Windows does not always make the responsible filter visible.

**Without auditing (default).** Most drops are answered definitively anyway: a duplicate
TCP segment or a bad checksum is the network stack discarding a packet, and no rule was
ever involved. For genuine filter decisions, the packet is matched against your rules on
direction, protocol, ports, addresses and program, and the likely rule is named. That
match is a lead rather than a verdict — conditions like `LocalSubnet` cannot be evaluated
offline, so results say so.

**With auditing on.** Windows records which filter actually acted, and the app resolves it
to that filter's name. This is authoritative, and it is also the only way to see
**outbound rule blocks at all**: those are refused before a packet exists, so traffic
capture never sees them. Turn it on from **Block auditing…** in the Traffic Monitor
toolbar.

> ⚠️ Block auditing changes a **system-wide Windows audit policy**, not an app setting. It
> persists after the app closes, and a machine dropping traffic steadily can write
> thousands of Security log entries an hour. Only failure auditing is ever enabled;
> success auditing would record every permitted connection. Turn it on to investigate,
> off afterwards.

### AI Connection (MCP)
WinFW Manager can expose its **live UI state** to an MCP client, so you can ask an AI about
what you are looking at instead of describing it or pasting screenshots.

Click **AI Connect** in the title bar, press **Start**, and run the command it shows:

```
claude mcp add --transport http winfw http://127.0.0.1:7337/mcp --header "Authorization: Bearer <token>"
```

Available tools:

| Tool | Purpose |
|------|---------|
| `get_current_view` | Active tab, its filters, and the rows actually visible right now |
| `get_adapters` | Adapters, optionally including hidden pseudo-adapters |
| `get_traffic_events` | Captured events, honouring the Traffic Monitor's filters |
| `get_dashboard` | Stats, top talkers, and the traffic-graph topology |
| `get_rules` | Firewall rules as loaded in Rules Manager |
| `explain_blocks` | Which rule most likely caused each recent drop |
| `get_audit_blocks` | Audited blocks with the filter that acted (needs Block auditing on) |
| `set_traffic_filter` / `clear_traffic_filters` | Drive the Traffic Monitor's filters |
| `select_tab` | Switch the active tab |

> **Security.** The app runs elevated, so the endpoint is **off until you start it**, binds to
> **127.0.0.1 only**, and requires a bearer token generated per run and never written to disk.
> There is **no firewall-write surface** — no tool can create, modify or delete a rule, so the
> worst an automated caller can do is change what is displayed.

## Quick Install

### Option 1: Download (Recommended)

1. Go to the [**Latest Release**](https://github.com/jola0909/WinFWManager/releases/latest)
2. Download **`WinFWManager-<version>-standalone.exe`** (~68 MB, no dependencies needed)
3. Right-click → **Run as Administrator**

That's it — single exe, nothing to install.

> 💡 **Smaller download?** Grab `WinFWManager-<version>-portable.exe` (~7 MB) instead, but you'll need the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed.

### Option 2: Build from Source

```bash
git clone https://github.com/jola0909/WinFWManager.git
cd WinFWManager
dotnet publish src/WinFWManager/WinFWManager.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The exe will be in `src/WinFWManager/bin/Release/net10.0-windows/win-x64/publish/`.

## Requirements

- **Windows 10/11** (or Windows Server 2016+)
- **Run as Administrator** — required for ETW traffic capture and firewall rule management

### GeoIP database (optional)

Country lookup needs MaxMind's free **GeoLite2 City** database, which is ~61 MB and cannot be
redistributed under MaxMind's licence — so it is **not** in this repo and not in the released
binaries. Without it the app runs normally and the Country column reads `Unknown` for every
public address.

To enable it, [sign up for a free MaxMind account](https://dev.maxmind.com/geoip/geolite2-free-geolocation-data),
download **GeoLite2-City.mmdb**, and either:

- drop it next to `WinFWManager.exe`, or
- place it at `src/WinFWManager/GeoLite2-City.mmdb` before building — the build copies it to the output automatically

## Architecture

```
WinFWManager/
├── src/
│   ├── WinFWManager/              # WPF UI (Views, ViewModels, Themes)
│   │   ├── Views/                 # XAML views for each tab
│   │   ├── ViewModels/            # MVVM ViewModels (CommunityToolkit.Mvvm)
│   │   ├── Converters/            # XAML value converters
│   │   ├── Themes/                # Dark theme resources
│   │   └── Assets/                # App icon
│   └── WinFWManager.Core/         # Core logic (services, models)
│       ├── Models/                 # Data models (FirewallRule, TrafficEvent, etc.)
│       ├── Services/               # ETW monitor, CIM queries, process resolver, GeoIP
│       └── Collections/            # RingBuffer for high-throughput event storage
```

**Key technologies:**
- **WPF** with MVVM pattern (CommunityToolkit.Mvvm)
- **CIM/WMI** via Microsoft.Management.Infrastructure for native firewall queries
- **ETW** via Microsoft.Diagnostics.Tracing.TraceEvent, using the `Microsoft-Windows-TCPIP` manifest provider for real-time traffic capture with allow/drop visibility
- **System.Reactive** for event batching and throttling
- **MaxMind GeoIP2** for IP geolocation (graceful fallback if DLL is blocked by WDAC)

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
