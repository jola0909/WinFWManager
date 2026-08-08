using System.Windows;

namespace WinFWManager.Mcp;

/// <summary>
/// Marshals MCP tool work onto the WPF dispatcher. Tools are invoked on Kestrel's
/// thread pool, but every ViewModel they read is bound to the UI and its
/// ObservableCollections are not safe to touch from another thread.
/// </summary>
internal static class Ui
{
    public static Task<T> RunAsync<T>(Func<T> func)
    {
        var app = Application.Current;
        if (app == null)
            return Task.FromResult(func());

        return app.Dispatcher.InvokeAsync(func).Task;
    }
}
