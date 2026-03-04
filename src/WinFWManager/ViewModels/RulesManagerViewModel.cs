using System.Collections.ObjectModel;
using System.ComponentModel;
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
    }

    partial void OnSelectedStoreChanged(FirewallStore value) => _ = RefreshRulesAsync();
    partial void OnSelectedProfileChanged(FirewallProfile? value) => RulesView.Refresh();
    partial void OnSearchTextChanged(string value) => RulesView.Refresh();
    partial void OnShowHyperVRulesChanged(bool value) => _ = RefreshRulesAsync();

    [RelayCommand]
    private async Task RefreshRulesAsync()
    {
        IsLoading = true;
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
            var match = rule.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                        (rule.Program?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!match) return false;
        }

        return true;
    }
}
