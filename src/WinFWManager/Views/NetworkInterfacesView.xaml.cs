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
    }
}
