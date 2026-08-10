using System.Collections.Specialized;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.ViewModels;

namespace WinFWManager.Views;

public partial class TrafficMonitorView : UserControl
{
    public TrafficMonitorView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<TrafficMonitorViewModel>();

        // The audit policy is system-wide and can change outside this app, so the label
        // is refreshed whenever the tab is shown rather than read once at startup.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && DataContext is TrafficMonitorViewModel vm)
                vm.RefreshAuditState();
        };
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        if (DataContext is TrafficMonitorViewModel vm)
        {
            vm.Events.CollectionChanged += OnEventsChanged;
        }
    }

    private void OnBlockAuditingClick(object sender, System.Windows.RoutedEventArgs e)
    {
        new WfpAuditDialog { Owner = System.Windows.Window.GetWindow(this) }.ShowDialog();

        // The dialog is where the policy is usually changed, so pick that up on close.
        if (DataContext is TrafficMonitorViewModel vm)
            vm.RefreshAuditState();
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is TrafficMonitorViewModel { IsAutoScroll: true } &&
            TrafficGrid.Items.Count > 0)
        {
            TrafficGrid.ScrollIntoView(TrafficGrid.Items[^1]);
        }
    }
}
