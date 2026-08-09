using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class RulesManagerViewModel : ObservableObject
{
    private readonly IFirewallRuleService _ruleService;

    public ObservableCollection<FirewallRuleInfo> Rules { get; } = new();
    public ICollectionView RulesView { get; }

    public Array StoreValues => Enum.GetValues(typeof(FirewallStore));
    public Array ProfileValues => Enum.GetValues(typeof(FirewallProfile));

    [ObservableProperty] private FirewallStore _selectedStore = FirewallStore.ActiveStore;
    [ObservableProperty] private FirewallProfile? _selectedProfile;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FirewallRuleInfo? _selectedRule;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _ruleCount;
    [ObservableProperty] private bool _showHyperVRules;

    public bool IsHyperVAvailable => _ruleService.IsHyperVFirewallAvailable;

    public RulesManagerViewModel(IFirewallRuleService ruleService)
    {
        _ruleService = ruleService;

        RulesView = CollectionViewSource.GetDefaultView(Rules);
        RulesView.Filter = FilterPredicate;

        _ = RefreshRulesAsync();
    }

    partial void OnSelectedStoreChanged(FirewallStore value) => _ = RefreshRulesAsync();
    partial void OnSelectedProfileChanged(FirewallProfile? value) => RulesView.Refresh();
    partial void OnSearchTextChanged(string value) => RulesView.Refresh();
    partial void OnShowHyperVRulesChanged(bool value) => _ = RefreshRulesAsync();

    [ObservableProperty] private string _errorMessage = string.Empty;

    [RelayCommand]
    private async Task RefreshRulesAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            Rules.Clear();

            IReadOnlyList<FirewallRuleInfo> rules;
            if (ShowHyperVRules && IsHyperVAvailable)
                rules = await _ruleService.GetHyperVRulesAsync();
            else
                rules = await _ruleService.GetRulesAsync(SelectedStore);

            foreach (var rule in rules)
                Rules.Add(rule);

            RuleCount = Rules.Count;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load rules: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        if (SelectedRule == null) return;
        await _ruleService.SetRuleEnabledAsync(SelectedRule.Name, !SelectedRule.Enabled);
        SelectedRule.Enabled = !SelectedRule.Enabled;
        RulesView.Refresh();
    }

    [RelayCommand]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule == null) return;

        var result = MessageBox.Show(
            $"Delete rule '{SelectedRule.DisplayName}'?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        await _ruleService.DeleteRuleAsync(SelectedRule.Name);
        Rules.Remove(SelectedRule);
        RuleCount = Rules.Count;
    }

    [RelayCommand]
    private void NewRule()
    {
        var dialog = new Views.RuleEditorDialog(new FirewallRuleInfo
        {
            Direction = TrafficDirection.Inbound,
            Action = TrafficAction.Block,
            Protocol = TransportProtocol.TCP,
            Profile = FirewallProfile.Any,
            Enabled = true
        });
        if (dialog.ShowDialog() == true)
        {
            _ = CreateRuleAsync(dialog.Rule);
        }
    }

    [RelayCommand]
    private void EditRule()
    {
        if (SelectedRule == null) return;
        var dialog = new Views.RuleEditorDialog(SelectedRule);
        if (dialog.ShowDialog() == true)
        {
            _ = UpdateRuleAsync(dialog.Rule);
        }
    }

    /// <summary>
    /// Writes the rules currently visible — search and profile filters applied — to CSV,
    /// which is the practical way to diff or document a machine's firewall config.
    /// </summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var rows = ((System.Collections.IEnumerable)RulesView).Cast<FirewallRuleInfo>().ToList();

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export rules",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"winfw-rules-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            // UTF-8 with BOM so non-ASCII rule and group names survive in Excel.
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));

            writer.WriteLine(Csv.Row(
                "Name", "Enabled", "Direction", "Action", "Protocol", "Local Port",
                "Remote Port", "Local Address", "Remote Address", "Profile", "Group",
                "Program", "Hyper-V", "Description"));

            foreach (var r in rows)
            {
                writer.WriteLine(Csv.Row(
                    r.DisplayName, r.Enabled, r.Direction, r.Action, r.Protocol,
                    r.LocalPort, r.RemotePort, r.LocalAddress, r.RemoteAddress,
                    r.Profile, r.Group, r.Program, r.IsHyperVRule, r.Description));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not write the file.\n\n{ex.Message}",
                "Export rules", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task CreateRuleAsync(FirewallRuleInfo rule)
    {
        await _ruleService.CreateRuleAsync(rule);
        await RefreshRulesAsync();
    }

    private async Task UpdateRuleAsync(FirewallRuleInfo rule)
    {
        await _ruleService.UpdateRuleAsync(rule);
        await RefreshRulesAsync();
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not FirewallRuleInfo rule) return false;

        if (SelectedProfile.HasValue && SelectedProfile.Value != FirewallProfile.Any &&
            rule.Profile != SelectedProfile.Value)
            return false;

        if (!string.IsNullOrEmpty(SearchText))
        {
            // Group is worth searching now that it resolves to a readable name:
            // Windows organises rules into groups like "Network Discovery", so it is
            // often the fastest way to pull a related set together.
            var match = rule.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                        (rule.Program?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (rule.Group?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!match) return false;
        }

        return true;
    }
}
