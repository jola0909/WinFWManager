namespace WinFWManager.Core.Models;

public class FirewallRuleInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public TrafficDirection Direction { get; set; }
    public TrafficAction Action { get; set; }
    public TransportProtocol Protocol { get; set; }
    public string? LocalPort { get; set; }
    public string? RemotePort { get; set; }
    public string? LocalAddress { get; set; }
    public string? RemoteAddress { get; set; }
    public FirewallProfile Profile { get; set; }
    public FirewallStore Store { get; set; }
    public string? InterfaceAlias { get; set; }
    public string? Program { get; set; }
    public string? Group { get; set; }
    public bool IsHyperVRule { get; set; }
}
