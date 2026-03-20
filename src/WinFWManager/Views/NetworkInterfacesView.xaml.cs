using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.ViewModels;

namespace WinFWManager.Views;

public partial class NetworkInterfacesView : UserControl
{
    public NetworkInterfacesView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<NetworkInterfacesViewModel>();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && DataContext is NetworkInterfacesViewModel vm)
        {
            await vm.OnActivatedAsync();
        }
    }
}
