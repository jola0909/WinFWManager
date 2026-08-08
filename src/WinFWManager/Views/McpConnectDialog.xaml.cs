using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.Mcp;

namespace WinFWManager.Views;

public partial class McpConnectDialog : Window
{
    private readonly McpServerHost _host;

    public McpConnectDialog()
    {
        InitializeComponent();
        _host = App.Services.GetRequiredService<McpServerHost>();
        Render();
    }

    private void Render()
    {
        var endpoint = _host.Endpoint;

        if (endpoint == null)
        {
            TxtStatus.Text = "Stopped";
            TxtStatusDetail.Text = "Listening on 127.0.0.1 only, and off until you start it.";
            BtnToggle.Content = "Start";
            PanelDetails.Visibility = Visibility.Collapsed;
            return;
        }

        TxtStatus.Text = "Running";
        TxtStatusDetail.Text = $"{endpoint.Url} — loopback only, bearer token required.";
        BtnToggle.Content = "Stop";
        TxtCommand.Text = endpoint.ClaudeCliCommand;
        PanelDetails.Visibility = Visibility.Visible;
    }

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        BtnToggle.IsEnabled = false;
        try
        {
            if (_host.IsRunning)
                await _host.StopAsync();
            else
                await _host.StartAsync();

            Render();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not start the MCP server.\n\n{ex.Message}",
                "AI Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            Render();
        }
        finally
        {
            BtnToggle.IsEnabled = true;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_host.Endpoint == null) return;

        try
        {
            Clipboard.SetText(_host.Endpoint.ClaudeCliCommand);
        }
        catch (Exception ex)
        {
            // Clipboard can be locked by another process.
            MessageBox.Show(this, ex.Message, "Copy failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
