using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.ViewModels;

namespace WinFWManager.Views;

public partial class LogViewerView : UserControl
{
    public LogViewerView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<LogViewerViewModel>();
    }
}
