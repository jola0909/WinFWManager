using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

public partial class NetworkInterfacesViewModel : ObservableObject
{
    private readonly INetworkInterfaceService _nicService;

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = new();

    [ObservableProperty] private NetworkAdapterInfo? _selectedAdapter;
    [ObservableProperty] private bool _isLoading;

    private bool _hasLoadedOnce;

    public NetworkInterfacesViewModel(INetworkInterfaceService nicService)
    {
        _nicService = nicService;
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
            var adapters = await _nicService.GetAllAdaptersAsync();
            Adapters.Clear();
            foreach (var adapter in adapters)
                Adapters.Add(adapter);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
