using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class NetworkInterfacesViewModel : ObservableObject
{
    private readonly INetworkInterfaceService _nicService;
    private readonly WslNetworkModeDetector _wslDetector;

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = new();

    [ObservableProperty] private NetworkAdapterInfo? _selectedAdapter;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _wslModeText = "";

    /// <summary>
    /// When false (the default) NDIS pseudo-adapters — WFP/QoS lightweight filter
    /// bindings and WAN miniports — are omitted, matching what <c>Get-NetAdapter</c>
    /// shows. A typical machine reports several dozen of these.
    /// </summary>
    [ObservableProperty] private bool _showHiddenAdapters;

    [ObservableProperty] private string _adapterCountText = "";

    private bool _hasLoadedOnce;
    private IReadOnlyList<NetworkAdapterInfo> _allAdapters = Array.Empty<NetworkAdapterInfo>();

    public NetworkInterfacesViewModel(INetworkInterfaceService nicService, WslNetworkModeDetector wslDetector)
    {
        _nicService = nicService;
        _wslDetector = wslDetector;
    }

    /// <summary>
    /// Called when the view becomes visible (tab selected).
    /// Triggers auto-refresh on first view, and can be called to refresh on subsequent visits.
    /// </summary>
    public async Task OnActivatedAsync()
    {
        if (!_hasLoadedOnce)
        {
            _hasLoadedOnce = true;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            await _nicService.RefreshAsync();
            _allAdapters = await _nicService.GetAllAdaptersAsync();
            ApplyAdapterFilter();

            // DetectMode/GetGuestIp may spawn wsl.exe (5s cap) — keep it off the UI thread.
            var wslText = await Task.Run(() =>
            {
                var mode = _wslDetector.DetectMode();
                var guestIp = mode is WslNetworkingMode.Nat or WslNetworkingMode.Bridged
                    ? _wslDetector.GetGuestIp() : null;
                return guestIp != null
                    ? $"WSL networking: {mode}  •  guest IP {guestIp}"
                    : $"WSL networking: {mode}";
            });
            WslModeText = wslText;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnShowHiddenAdaptersChanged(bool value) => ApplyAdapterFilter();

    private void ApplyAdapterFilter()
    {
        var visible = ShowHiddenAdapters
            ? _allAdapters
            : _allAdapters.Where(a => !a.IsHidden).ToList();

        Adapters.Clear();
        foreach (var adapter in visible)
            Adapters.Add(adapter);

        var hidden = _allAdapters.Count(a => a.IsHidden);
        AdapterCountText = hidden > 0 && !ShowHiddenAdapters
            ? $"{Adapters.Count} adapters  •  {hidden} hidden"
            : $"{Adapters.Count} adapters";
    }
}
