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
        => new WfpAuditDialog { Owner = System.Windows.Window.GetWindow(this) }.ShowDialog();

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is TrafficMonitorViewModel { IsAutoScroll: true } &&
            TrafficGrid.Items.Count > 0)
        {
            TrafficGrid.ScrollIntoView(TrafficGrid.Items[^1]);
        }
    }
}
