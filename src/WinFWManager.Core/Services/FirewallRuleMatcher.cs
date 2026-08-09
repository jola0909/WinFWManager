using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>Why a packet was most likely dropped, and the rules behind that conclusion.</summary>
public sealed record RuleAttribution(
    string Summary,
    IReadOnlyList<FirewallRuleInfo> BlockingRules,
    IReadOnlyList<FirewallRuleInfo> AllowingRules,
    bool IsConclusive);

/// <summary>
/// Works out which firewall rule most likely dropped a packet, by matching it against
/// the configured rules.
///
/// This is an approximation and is presented as one. The authoritative answer lives in
/// the WFP filter that actually ran, whose id only reaches user space through Security
/// audit events 5152/5157 — off by default, and enabling them is a system-wide audit
/// policy change. So instead the packet is matched against the same rule set the user
/// can see and edit, which is also the form an answer is actionable in.
///
/// Known limits, all reflected in <see cref="RuleAttribution.IsConclusive"/>:
/// rule conditions that cannot be evaluated here (keyword addresses like LocalSubnet,
/// service-based rules, interface types) are treated as "might match" rather than
/// discarded, so a listed rule is a candidate and not a verdict; and WFP arbitrates
/// across sublayers with dynamic filters that have no rule at all behind them.
/// </summary>
public static class FirewallRuleMatcher
{
    /// <summary>
    /// Explains a dropped event. Rules are matched on direction, protocol, ports,
    /// addresses and program; Block rules take precedence over Allow, as in Windows.
    /// </summary>
    public static RuleAttribution Explain(
        TrafficEvent evt, IReadOnlyList<FirewallRuleInfo> rules)
    {
        if (evt.Action == TrafficAction.Allow)
            return new RuleAttribution("This packet was allowed.", [], [], true);

        var candidates = rules.Where(r => Matches(r, evt)).ToList();

        var blocking = candidates
            .Where(r => r.Action is TrafficAction.Block or TrafficAction.Drop)
            .OrderByDescending(Specificity)
            .ToList();

        var allowing = candidates
            .Where(r => r.Action == TrafficAction.Allow)
            .OrderByDescending(Specificity)
            .ToList();

        if (blocking.Count > 0)
        {
            var lead = blocking[0];
            var extra = blocking.Count > 1 ? $" (+{blocking.Count - 1} other matching)" : "";
            return new RuleAttribution(
                $"Likely blocked by rule \"{lead.DisplayName}\"{extra}.",
                blocking, allowing, IsConclusive: blocking.Count == 1);
        }

        // Nothing blocks it explicitly. Inbound is deny-by-default, so an absent Allow
        // rule is itself the explanation; outbound is allow-by-default, so silence there
        // means the drop came from somewhere this matcher cannot see.
        if (allowing.Count == 0)
        {
            return evt.Direction == TrafficDirection.Inbound
                ? new RuleAttribution(
                    "No inbound rule allows this. Inbound traffic is blocked unless a rule permits it.",
                    [], [], IsConclusive: true)
                : new RuleAttribution(
                    "No rule matches this packet. Outbound traffic is allowed by default, so the drop " +
                    "likely came from the network stack rather than a firewall rule — check the Reason column.",
                    [], [], IsConclusive: false);
        }

        return new RuleAttribution(
            $"No blocking rule matches, but {allowing.Count} allow rule(s) do. The drop likely came from " +
            "a filter with no rule behind it, such as a Hyper-V or WFP dynamic filter.",
            [], allowing, IsConclusive: false);
    }

    private static bool Matches(FirewallRuleInfo rule, TrafficEvent evt)
        => rule.Enabled
        && rule.Direction == evt.Direction
        && ProtocolMatches(rule.Protocol, evt.Protocol)
        && ProfileMatches(rule.Profile, evt.Profile)
        && PortMatches(rule.LocalPort, evt.LocalPort)
        && PortMatches(rule.RemotePort, evt.RemotePort)
        && AddressMatches(rule.LocalAddress, evt.LocalAddress)
        && AddressMatches(rule.RemoteAddress, evt.RemoteAddress)
        && ProgramMatches(rule.Program, evt.ProcessName);

    private static bool ProtocolMatches(TransportProtocol rule, TransportProtocol evt)
        // "Other" covers every protocol this app does not name, including the "any"
        // rules, so it cannot be used to exclude anything.
        => rule == TransportProtocol.Other || rule == evt;

    private static bool ProfileMatches(FirewallProfile rule, FirewallProfile evt)
        => rule == FirewallProfile.Any || evt == FirewallProfile.Any || rule == evt;

    /// <summary>
    /// Matches a rule's port specification: empty or "Any" is a wildcard, and a list of
    /// ports and ranges ("80,443,8000-8100") matches any member. Service keywords such
    /// as "RPC" cannot be resolved here and are treated as wildcards, which keeps the
    /// rule as a candidate rather than silently dropping it.
    /// </summary>
    public static bool PortMatches(string? spec, int port)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Equals("Any", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part[..dash], out var lo) && int.TryParse(part[(dash + 1)..], out var hi)
                    && port >= lo && port <= hi)
                    return true;
                continue;
            }

            if (int.TryParse(part, out var single))
            {
                if (single == port) return true;
                continue;
            }

            return true; // unresolvable keyword — keep the rule in play
        }

        return false;
    }

    /// <summary>
    /// Matches a rule's address specification. Empty or "Any" is a wildcard, plain
    /// addresses and CIDR are evaluated, and keywords like "LocalSubnet" or "Internet"
    /// are treated as wildcards since they depend on live network state.
    /// </summary>
    public static bool AddressMatches(string? spec, IPAddress? address)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Equals("Any", StringComparison.OrdinalIgnoreCase))
            return true;

        if (address == null)
            return true;

        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = part.IndexOf('/');
            if (slash > 0)
            {
                if (IPAddress.TryParse(part[..slash], out var network)
                    && int.TryParse(part[(slash + 1)..], out var prefix)
                    && new IpSubnet(network, prefix).Contains(address))
                    return true;
                continue;
            }

            if (IPAddress.TryParse(part, out var exact))
            {
                if (exact.Equals(address)) return true;
                continue;
            }

            return true; // keyword such as LocalSubnet — keep the rule in play
        }

        return false;
    }

    /// <summary>
    /// Matches a rule's program path against a captured process name. Only the file name
    /// is compared, because capture yields a name while rules store a full path with
    /// environment variables ("%SystemRoot%\system32\svchost.exe").
    /// </summary>
    public static bool ProgramMatches(string? rulePath, string? processName)
    {
        if (string.IsNullOrWhiteSpace(rulePath)
            || rulePath.Equals("Any", StringComparison.OrdinalIgnoreCase)
            || rulePath.Equals("System", StringComparison.OrdinalIgnoreCase))
            return true;

        // No process on the event (kernel drops carry no PID) — cannot rule it out.
        if (string.IsNullOrWhiteSpace(processName))
            return true;

        var separator = rulePath.LastIndexOfAny(['\\', '/']);
        var fileName = separator >= 0 ? rulePath[(separator + 1)..] : rulePath;

        return TrimExe(fileName).Equals(TrimExe(processName), StringComparison.OrdinalIgnoreCase);

        static string TrimExe(string value)
            => value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }

    /// <summary>
    /// How constrained a rule is. A rule naming a program and a port describes the
    /// packet far better than a blanket one, so it is the likelier explanation.
    /// </summary>
    private static int Specificity(FirewallRuleInfo rule)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(rule.Program)) score += 4;
        if (HasValue(rule.RemotePort)) score += 2;
        if (HasValue(rule.LocalPort)) score += 2;
        if (HasValue(rule.RemoteAddress)) score += 1;
        if (HasValue(rule.LocalAddress)) score += 1;
        if (rule.Protocol != TransportProtocol.Other) score += 1;
        return score;

        static bool HasValue(string? s)
            => !string.IsNullOrWhiteSpace(s) && !s.Equals("Any", StringComparison.OrdinalIgnoreCase);
    }
}
