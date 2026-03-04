using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.ViewModels;
using WinFWManager.Core.Services;

namespace WinFWManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(App.Services.GetRequiredService<IEtwTrafficMonitor>());
    }
}
