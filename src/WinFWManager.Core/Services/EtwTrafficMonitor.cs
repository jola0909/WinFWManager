using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class EtwTrafficMonitor : IEtwTrafficMonitor
{
    private TraceEventSession? _session;
    private Thread? _processingThread;
    private readonly Subject<TrafficEvent> _subject = new();
    private volatile bool _isRunning;
    private const string SessionName = "WinFWManagerETW";
    private static readonly Guid WfpProviderGuid = new("0c478c5b-0351-41b1-8c58-4a6737da32e3");

    public IObservable<TrafficEvent> TrafficEvents => _subject.AsObservable();
    public bool IsRunning => _isRunning;
    public bool RequiresAdmin => !IsAdmin();

    public void Start()
    {
        if (_isRunning) return;
        if (!IsAdmin())
            throw new UnauthorizedAccessException("ETW requires administrator privileges.");

        // Clean up any previous session
        try { TraceEventSession.GetActiveSession(SessionName)?.Dispose(); } catch { }

        _session = new TraceEventSession(SessionName);
        _session.EnableProvider(WfpProviderGuid);

        _isRunning = true;
        _processingThread = new Thread(ProcessEvents)
        {
            IsBackground = true,
            Name = "ETW-WFP-Processor"
        };
        _processingThread.Start();
    }

    public void Stop()
    {
        _isRunning = false;
        _session?.Stop();
        _session?.Dispose();
        _session = null;
    }

    public void Dispose()
    {
        Stop();
        _subject.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ProcessEvents()
    {
        if (_session == null) return;

        _session.Source.Dynamic.All += (TraceEvent data) =>
        {
            try
            {
                var evt = MapTraceEvent(data);
                if (evt != null)
                    _subject.OnNext(evt);
            }
            catch { /* skip malformed events */ }
        };

        _session.Source.Process();
    }

    private static TrafficEvent? MapTraceEvent(TraceEvent data)
    {
        // TODO: Refine WFP event mapping by inspecting live events.
        // The exact payload depends on the WFP event type/opcode.
        // For now, extract what we can from every event.
        try
        {
            return new TrafficEvent
            {
                Timestamp = data.TimeStamp,
                ProcessId = data.ProcessID,
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
