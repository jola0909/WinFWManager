using System.Collections.Concurrent;
using System.Diagnostics;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class ProcessResolver : IProcessResolver
{
    private readonly ConcurrentDictionary<int, (ProcessInfo Info, DateTime CachedAt)> _cache = new();
    private readonly int _cacheTtlSeconds;

    public ProcessResolver(int cacheTtlSeconds = 300)
    {
        _cacheTtlSeconds = cacheTtlSeconds;
    }

    public ProcessInfo Resolve(int processId)
    {
        if (_cache.TryGetValue(processId, out var cached))
        {
            if ((DateTime.UtcNow - cached.CachedAt).TotalSeconds < _cacheTtlSeconds)
                return cached.Info;
            _cache.TryRemove(processId, out _);
        }

        var info = ResolveInternal(processId);
        _cache[processId] = (info, DateTime.UtcNow);
        return info;
    }

    public void ClearCache() => _cache.Clear();

    private static ProcessInfo ResolveInternal(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return new ProcessInfo
            {
                ProcessId = processId,
                Name = process.ProcessName,
                Path = TryGetProcessPath(process),
                ResolvedAt = DateTime.UtcNow,
                IsExited = false
            };
        }
        catch (ArgumentException)
        {
            return new ProcessInfo
            {
                ProcessId = processId,
                ResolvedAt = DateTime.UtcNow,
                IsExited = true
            };
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }
}
