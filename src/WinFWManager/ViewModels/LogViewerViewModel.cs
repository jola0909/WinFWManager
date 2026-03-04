using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class LogViewerViewModel : ObservableObject
{
    private readonly IFirewallLogParser _parser;

    public ObservableCollection<TrafficEvent> Events { get; } = new();
    public ICollectionView EventsView { get; }

    [ObservableProperty] private string _filterSourceIp = string.Empty;
    [ObservableProperty] private string _filterDestIp = string.Empty;
    [ObservableProperty] private string _filterProtocol = string.Empty;
    [ObservableProperty] private string _filterProcess = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _loadProgress;
    [ObservableProperty] private string? _loadedFilePath;
    [ObservableProperty] private DateTime? _filterStartDate;
    [ObservableProperty] private DateTime? _filterEndDate;
    [ObservableProperty] private int _eventCount;

    public LogViewerViewModel(IFirewallLogParser parser)
    {
        _parser = parser;

        EventsView = CollectionViewSource.GetDefaultView(Events);
        EventsView.Filter = FilterPredicate;
    }

    partial void OnFilterSourceIpChanged(string value) => EventsView.Refresh();
    partial void OnFilterDestIpChanged(string value) => EventsView.Refresh();
    partial void OnFilterProtocolChanged(string value) => EventsView.Refresh();
    partial void OnFilterProcessChanged(string value) => EventsView.Refresh();
    partial void OnFilterStartDateChanged(DateTime? value) => EventsView.Refresh();
    partial void OnFilterEndDateChanged(DateTime? value) => EventsView.Refresh();

    [RelayCommand]
    private async Task LoadFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Firewall Log",
            Filter = "Log Files (*.log)|*.log|All Files (*.*)|*.*",
            DefaultExt = ".log"
        };

        if (dialog.ShowDialog() != true) return;

        IsLoading = true;
        LoadProgress = 0;
        Events.Clear();

        try
        {
            var progress = new Progress<int>(p => LoadProgress = p);
            var events = await _parser.ParseFileAsync(dialog.FileName, progress);

            foreach (var evt in events)
                Events.Add(evt);

            LoadedFilePath = dialog.FileName;
            EventCount = events.Count;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not TrafficEvent evt) return false;

        if (!string.IsNullOrEmpty(FilterSourceIp) &&
            evt.SourceAddress?.ToString().Contains(FilterSourceIp, StringComparison.OrdinalIgnoreCase) != true)
            return false;

        if (!string.IsNullOrEmpty(FilterDestIp) &&
            evt.DestinationAddress?.ToString().Contains(FilterDestIp, StringComparison.OrdinalIgnoreCase) != true)
            return false;

        if (!string.IsNullOrEmpty(FilterProtocol) &&
            !evt.Protocol.ToString().Contains(FilterProtocol, StringComparison.OrdinalIgnoreCase))
            return false;

        if (FilterStartDate.HasValue && evt.Timestamp < FilterStartDate.Value)
            return false;

        if (FilterEndDate.HasValue && evt.Timestamp > FilterEndDate.Value)
            return false;

        return true;
    }
}
