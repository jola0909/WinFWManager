using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace WinFWManager.Core.Services;

public class PowerShellRunspacePool : IDisposable
{
    private readonly RunspacePool _pool;

    public PowerShellRunspacePool(int minRunspaces = 1, int maxRunspaces = 5)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ImportPSModule(new[] { "NetSecurity" });
        _pool = RunspaceFactory.CreateRunspacePool(iss);
        _pool.SetMinRunspaces(minRunspaces);
        _pool.SetMaxRunspaces(maxRunspaces);
        _pool.Open();
    }

    public async Task<IReadOnlyList<T>> InvokeAsync<T>(string script, Dictionary<string, object>? parameters = null)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _pool;
        ps.AddScript(script);

        if (parameters != null)
        {
            foreach (var p in parameters)
                ps.AddParameter(p.Key, p.Value);
        }

        var results = await Task.Run(() => ps.Invoke<T>());

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
            throw new InvalidOperationException($"PowerShell error: {errors}");
        }

        return results.ToList().AsReadOnly();
    }

    public async Task InvokeAsync(string script, Dictionary<string, object>? parameters = null)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _pool;
        ps.AddScript(script);

        if (parameters != null)
        {
            foreach (var p in parameters)
                ps.AddParameter(p.Key, p.Value);
        }

        await Task.Run(() => ps.Invoke());

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
            throw new InvalidOperationException($"PowerShell error: {errors}");
        }
    }

    public void Dispose()
    {
        _pool.Close();
        _pool.Dispose();
        GC.SuppressFinalize(this);
    }
}
