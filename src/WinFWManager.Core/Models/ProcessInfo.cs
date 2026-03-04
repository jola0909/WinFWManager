namespace WinFWManager.Core.Models;

public class ProcessInfo
{
    public int ProcessId { get; set; }
    public string Name { get; set; } = "Unknown";
    public string? Path { get; set; }
    public DateTime ResolvedAt { get; set; }
    public bool IsExited { get; set; }

    public string DisplayName => IsExited ? $"PID {ProcessId} (exited)" : Name;
}
