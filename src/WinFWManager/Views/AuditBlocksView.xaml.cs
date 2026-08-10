using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.ViewModels;

namespace WinFWManager.Views;

public partial class AuditBlocksView : UserControl
{
    private bool _loadedOnce;

    public AuditBlocksView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<AuditBlocksViewModel>();

        // Reading the Security log is slow, so it happens on first visit rather than at
        // startup, and again on later visits only via Refresh.
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true && !_loadedOnce && DataContext is AuditBlocksViewModel vm)
            {
                _loadedOnce = true;
                await vm.RefreshCommand.ExecuteAsync(null);
            }
        };
    }
}
