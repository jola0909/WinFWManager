using System.Windows;
using WinFWManager.Core.Models;

namespace WinFWManager.Views;

public partial class RuleEditorDialog : Window
{
    public FirewallRuleInfo Rule { get; }

    public RuleEditorDialog(FirewallRuleInfo rule)
    {
        InitializeComponent();
        Rule = rule;

        // Populate fields from rule
        TxtName.Text = rule.Name;
        TxtDisplayName.Text = rule.DisplayName;
        TxtDescription.Text = rule.Description ?? string.Empty;
        CmbDirection.SelectedItem = rule.Direction;
        CmbAction.SelectedItem = rule.Action;
        CmbProtocol.SelectedItem = rule.Protocol;
        TxtLocalPort.Text = rule.LocalPort ?? string.Empty;
        TxtRemotePort.Text = rule.RemotePort ?? string.Empty;
        TxtLocalAddress.Text = rule.LocalAddress ?? string.Empty;
        TxtRemoteAddress.Text = rule.RemoteAddress ?? string.Empty;
        TxtProgram.Text = rule.Program ?? string.Empty;
        CmbProfile.SelectedItem = rule.Profile;
        ChkEnabled.IsChecked = rule.Enabled;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Rule.Name = TxtName.Text;
        Rule.DisplayName = TxtDisplayName.Text;
        Rule.Description = string.IsNullOrWhiteSpace(TxtDescription.Text) ? null : TxtDescription.Text;
        Rule.Direction = (TrafficDirection)CmbDirection.SelectedItem;
        Rule.Action = (TrafficAction)CmbAction.SelectedItem;
        Rule.Protocol = (TransportProtocol)CmbProtocol.SelectedItem;
        Rule.LocalPort = string.IsNullOrWhiteSpace(TxtLocalPort.Text) ? null : TxtLocalPort.Text;
        Rule.RemotePort = string.IsNullOrWhiteSpace(TxtRemotePort.Text) ? null : TxtRemotePort.Text;
        Rule.LocalAddress = string.IsNullOrWhiteSpace(TxtLocalAddress.Text) ? null : TxtLocalAddress.Text;
        Rule.RemoteAddress = string.IsNullOrWhiteSpace(TxtRemoteAddress.Text) ? null : TxtRemoteAddress.Text;
        Rule.Program = string.IsNullOrWhiteSpace(TxtProgram.Text) ? null : TxtProgram.Text;
        Rule.Profile = (FirewallProfile)CmbProfile.SelectedItem;
        Rule.Enabled = ChkEnabled.IsChecked == true;

        DialogResult = true;
    }
}
