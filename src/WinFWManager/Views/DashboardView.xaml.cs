using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.ViewModels;

namespace WinFWManager.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DashboardViewModel>();
    }
}
