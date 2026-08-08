using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.ViewModels;

namespace WinFWManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }

    private void OnAiConnectClick(object sender, RoutedEventArgs e)
        => new Views.McpConnectDialog { Owner = this }.ShowDialog();
}
