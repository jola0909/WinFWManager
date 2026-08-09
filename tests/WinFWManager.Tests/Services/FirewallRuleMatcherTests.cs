using System.Net;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class FirewallRuleMatcherTests
{
    private static TrafficEvent Drop(
        TrafficDirection direction = TrafficDirection.Inbound,
        TransportProtocol protocol = TransportProtocol.TCP,
        string source = "203.0.113.5", int sourcePort = 51000,
        string destination = "192.168.1.51", int destinationPort = 445,
        string? process = "svchost")
        => new()
        {
            Action = TrafficAction.Drop,
            Direction = direction,
            Protocol = protocol,
            SourceAddress = IPAddress.Parse(source),
            SourcePort = sourcePort,
            DestinationAddress = IPAddress.Parse(destination),
            DestinationPort = destinationPort,
            ProcessName = process,
            Profile = FirewallProfile.Private,
        };

    private static FirewallRuleInfo Rule(
        string name, TrafficAction action = TrafficAction.Block,
        TrafficDirection direction = TrafficDirection.Inbound,
        TransportProtocol protocol = TransportProtocol.TCP,
        string localPort = "", string remotePort = "",
        string localAddress = "", string remoteAddress = "",
        string program = "", bool enabled = true)
        => new()
        {
            DisplayName = name, Action = action, Direction = direction, Protocol = protocol,
            LocalPort = localPort, RemotePort = remotePort,
            LocalAddress = localAddress, RemoteAddress = remoteAddress,
            Program = program, Enabled = enabled, Profile = FirewallProfile.Any,
        };

    [Fact]
    public void Explain_AllowedEvent_SaysSo()
    {
        var evt = Drop();
        evt.Action = TrafficAction.Allow;

        FirewallRuleMatcher.Explain(evt, []).Summary.Should().Contain("allowed");
    }

    [Theory]
    [InlineData("Duplicate segment")]
    [InlineData("Bad checksum")]
    [InlineData("Endpoint not found (no listener)")]
    [InlineData("No route")]
    public void Explain_StackLevelDrop_SaysNoRuleIsInvolved(string reason)
    {
        // These never involved a rule, so searching the rule set would produce
        // confident-looking noise. Answer directly instead.
        var evt = Drop();
        evt.DropReason = reason;

        var rules = new[] { Rule("Block everything") };
        var result = FirewallRuleMatcher.Explain(evt, rules);

        result.Summary.Should().Contain("network stack");
        result.BlockingRules.Should().BeEmpty();
        result.IsConclusive.Should().BeTrue();
    }

    [Theory]
    [InlineData("Firewall (WFP filter)")]
    [InlineData("Inspection drop (WFP)")]
    [InlineData("Administratively prohibited")]
    public void Explain_PolicyDrop_StillConsultsTheRules(string reason)
    {
        var evt = Drop(destinationPort: 445);
        evt.DropReason = reason;

        var rules = new[] { Rule("Block SMB", localPort: "445") };

        FirewallRuleMatcher.Explain(evt, rules).BlockingRules.Should().ContainSingle();
    }

    [Fact]
    public void Explain_MatchingBlockRule_NamesIt()
    {
        var rules = new[] { Rule("Block SMB", localPort: "445") };

        var result = FirewallRuleMatcher.Explain(Drop(destinationPort: 445), rules);

        result.Summary.Should().Contain("Block SMB");
        result.BlockingRules.Should().ContainSingle();
        result.IsConclusive.Should().BeTrue();
    }

    [Fact]
    public void Explain_DisabledRule_IsIgnored()
    {
        var rules = new[] { Rule("Block SMB", localPort: "445", enabled: false) };

        FirewallRuleMatcher.Explain(Drop(destinationPort: 445), rules)
            .BlockingRules.Should().BeEmpty();
    }

    [Fact]
    public void Explain_RuleForOtherDirection_IsIgnored()
    {
        var rules = new[] { Rule("Outbound block", direction: TrafficDirection.Outbound, localPort: "445") };

        FirewallRuleMatcher.Explain(Drop(direction: TrafficDirection.Inbound, destinationPort: 445), rules)
            .BlockingRules.Should().BeEmpty();
    }

    [Fact]
    public void Explain_InboundWithNoRules_BlamesDefaultDeny()
    {
        var result = FirewallRuleMatcher.Explain(Drop(direction: TrafficDirection.Inbound), []);

        result.Summary.Should().Contain("blocked unless a rule permits");
        result.IsConclusive.Should().BeTrue();
    }

    [Fact]
    public void Explain_OutboundWithNoRules_IsInconclusive()
    {
        // Outbound is allow-by-default, so silence here means the drop came from
        // somewhere this matcher cannot see — it must not claim otherwise.
        var result = FirewallRuleMatcher.Explain(Drop(direction: TrafficDirection.Outbound), []);

        result.IsConclusive.Should().BeFalse();
        result.Summary.Should().Contain("allowed by default");
    }

    [Fact]
    public void Explain_SeveralBlockingRules_LeadsWithTheMostSpecific()
    {
        var rules = new[]
        {
            Rule("Broad block"),
            Rule("Specific block", localPort: "445", program: @"%SystemRoot%\system32\svchost.exe"),
        };

        var result = FirewallRuleMatcher.Explain(Drop(destinationPort: 445), rules);

        result.BlockingRules[0].DisplayName.Should().Be("Specific block");
        result.Summary.Should().Contain("+1 other matching");
        result.IsConclusive.Should().BeFalse();   // more than one candidate
    }

    [Fact]
    public void Explain_OnlyAllowRulesMatch_IsInconclusive()
    {
        var rules = new[] { Rule("Allow SMB", action: TrafficAction.Allow, localPort: "445") };

        var result = FirewallRuleMatcher.Explain(Drop(destinationPort: 445), rules);

        result.BlockingRules.Should().BeEmpty();
        result.AllowingRules.Should().ContainSingle();
        result.IsConclusive.Should().BeFalse();
    }

    // ---- condition matching ----

    [Theory]
    [InlineData("", 443, true)]
    [InlineData("Any", 443, true)]
    [InlineData("443", 443, true)]
    [InlineData("443", 80, false)]
    [InlineData("80,443,8080", 443, true)]
    [InlineData("80,443,8080", 8081, false)]
    [InlineData("8000-8100", 8050, true)]
    [InlineData("8000-8100", 8101, false)]
    [InlineData("RPC", 49152, true)]     // unresolvable keyword stays a candidate
    public void PortMatches_HandlesListsRangesAndKeywords(string spec, int port, bool expected)
    {
        FirewallRuleMatcher.PortMatches(spec, port).Should().Be(expected);
    }

    [Theory]
    [InlineData("", "203.0.113.5", true)]
    [InlineData("Any", "203.0.113.5", true)]
    [InlineData("203.0.113.5", "203.0.113.5", true)]
    [InlineData("203.0.113.6", "203.0.113.5", false)]
    [InlineData("203.0.113.0/24", "203.0.113.5", true)]
    [InlineData("198.51.100.0/24", "203.0.113.5", false)]
    [InlineData("LocalSubnet", "203.0.113.5", true)]   // keyword stays a candidate
    public void AddressMatches_HandlesExactCidrAndKeywords(string spec, string address, bool expected)
    {
        FirewallRuleMatcher.AddressMatches(spec, IPAddress.Parse(address)).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\system32\svchost.exe", "svchost", true)]
    [InlineData(@"%SystemRoot%\system32\svchost.exe", "svchost.exe", true)]
    [InlineData(@"C:\Program Files\app\app.exe", "svchost", false)]
    [InlineData("", "svchost", true)]
    [InlineData(@"%SystemRoot%\system32\svchost.exe", null, true)]  // PID-less drop
    public void ProgramMatches_ComparesFileNameOnly(string? rulePath, string? process, bool expected)
    {
        FirewallRuleMatcher.ProgramMatches(rulePath, process).Should().Be(expected);
    }
}
