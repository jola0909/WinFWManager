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

    public NetworkInterfacesViewModel(INetworkInterfaceService nicService)
    {
        _nicService = nicService;
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
