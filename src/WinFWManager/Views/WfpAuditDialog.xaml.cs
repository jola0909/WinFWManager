using System.Windows;
using WinFWManager.Core.Services;

namespace WinFWManager.Views;

public partial class WfpAuditDialog : Window
{
    public WfpAuditDialog()
    {
        InitializeComponent();
        Render();
    }

    private void Render()
    {
        var state = WfpAuditPolicy.GetState();

        switch (state)
        {
            case WfpAuditState.FailureAudit:
                TxtStatus.Text = "Auditing blocked traffic";
                TxtStatusDetail.Text = "Blocks are being written to the Security log, so the rule " +
                                       "behind them can be identified.";
                BtnToggle.Content = "Disable";
                BtnToggle.IsEnabled = true;
                break;

            case WfpAuditState.Disabled:
                TxtStatus.Text = "Not auditing";
                TxtStatusDetail.Text = "Blocks are not recorded, so the responsible rule cannot be identified.";
                BtnToggle.Content = "Enable";
                BtnToggle.IsEnabled = true;
                break;

            default:
                // Never present an unreadable policy as "off" — that would invite
                // switching on something that is already on.
                TxtStatus.Text = "Cannot read the audit policy";
                TxtStatusDetail.Text = "This needs Administrator rights. Restart WinFW Manager elevated to " +
                                       "check or change it.";
                BtnToggle.Content = "Enable";
                BtnToggle.IsEnabled = false;
                break;
        }
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        var enabling = WfpAuditPolicy.GetState() != WfpAuditState.FailureAudit;

        if (enabling)
        {
            var confirm = MessageBox.Show(this,
                "Enable failure auditing for the Filtering Platform subcategories?\n\n" +
                "This changes a system-wide Windows security setting, not a setting of this app. " +
                "It stays on after you close WinFW Manager, and a machine dropping traffic steadily " +
                "can write thousands of Security log entries an hour.",
                "Enable block auditing", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.OK)
                return;
        }

        try
        {
            WfpAuditPolicy.SetEnabled(enabling);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not change the audit policy.\n\n{ex.Message}",
                "Block auditing", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Render();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
