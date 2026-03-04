using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.ViewModels;

namespace WinFWManager.Views;

public partial class RulesManagerView : UserControl
{
    public RulesManagerView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<RulesManagerViewModel>();
    }
}
