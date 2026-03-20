using System.Diagnostics;
using System.Text;

namespace WinFWManager.Core.Services;

/// <summary>
/// Executes PowerShell scripts via the native Windows PowerShell 5.1 subprocess.
/// This ensures full compatibility with CDXML-based modules like NetSecurity.
/// </summary>
public class PowerShellRunspacePool : IDisposable
{
    private static readonly string PowerShellExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        @"WindowsPowerShell\v1.0\powershell.exe");

    public async Task<string> InvokeAsync(string script, Dictionary<string, object>? parameters = null)
    {
        var fullScript = new StringBuilder();
        if (parameters != null)
        {
            foreach (var (key, value) in parameters)
            {
                var escaped = value?.ToString()?.Replace("'", "''") ?? "";
                fullScript.AppendLine($"${key} = '{escaped}'");
            }
        }
        fullScript.Append(script);

        var encoded = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(fullScript.ToString()));

        var psi = new ProcessStartInfo
        {
            FileName = PowerShellExe,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read stdout and stderr concurrently to avoid deadlocks
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        var output = await outputTask;
        var error = await errorTask;
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var msg = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            throw new InvalidOperationException($"PowerShell error: {msg}");
        }

        return output;
    }

    public void Dispose() { }
}
