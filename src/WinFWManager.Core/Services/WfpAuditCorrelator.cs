using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

/// <summary>
/// Pairs a captured drop with the Security audit event describing the same packet.
///
/// The two sources see different things. ETW capture has rich per-packet detail but no
/// idea which filter acted; the audit event names the filter but exists only while
/// auditing is on. Matching them gives an authoritative answer for the drops that appear
/// in both.
/// </summary>
public static class WfpAuditCorrelator
{
    /// <summary>
    /// Audit timestamps have one-second resolution and the two sources record a packet at
    /// slightly different points, so an exact match is not possible. The endpoints must
    /// agree exactly; time only has to be close.
    /// </summary>
    private static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The audit event describing this packet, or null when none matches — which is the
    /// normal case for outbound blocks, since those never appear in the capture at all.
    /// </summary>
    public static WfpAuditBlock? FindMatch(TrafficEvent evt, IReadOnlyList<WfpAuditBlock> audits)
    {
        foreach (var audit in audits)
        {
            if (Math.Abs((audit.Time - evt.Timestamp).TotalSeconds) > MaxSkew.TotalSeconds)
                continue;

            if (!EndpointsMatch(evt, audit))
                continue;

            return audit;
        }

        return null;
    }

    private static bool EndpointsMatch(TrafficEvent evt, WfpAuditBlock audit)
    {
        var source = evt.SourceAddress?.ToString();
        var destination = evt.DestinationAddress?.ToString();

        // Compare in both orientations: the audit event labels endpoints from the
        // filter's point of view, which is not always the capture's.
        var forward = Same(source, audit.SourceAddress)
                   && Same(destination, audit.DestAddress)
                   && SamePort(evt.SourcePort, audit.SourcePort)
                   && SamePort(evt.DestinationPort, audit.DestPort);

        var reversed = Same(source, audit.DestAddress)
                    && Same(destination, audit.SourceAddress)
                    && SamePort(evt.SourcePort, audit.DestPort)
                    && SamePort(evt.DestinationPort, audit.SourcePort);

        return forward || reversed;
    }

    private static bool Same(string? a, string? b)
        => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SamePort(int port, string? text)
        => int.TryParse(text, out var value) && value == port;
}
