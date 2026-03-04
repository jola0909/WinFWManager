using System.Net;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class FirewallLogParser : IFirewallLogParser
{
    public async Task<IReadOnlyList<TrafficEvent>> ParseFileAsync(
        string filePath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        var events = new List<TrafficEvent>();
        var dataLines = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#')).ToArray();
        int total = dataLines.Length;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evt = ParseLine(dataLines[i]);
            if (evt != null)
                events.Add(evt);

            if (progress != null && (i % 100 == 0 || i == total - 1))
                progress.Report((int)((i + 1) * 100.0 / total));
        }

        return events.AsReadOnly();
    }

    private static TrafficEvent? ParseLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 17) return null;

        try
        {
            var direction = parts[16].Equals("SEND", StringComparison.OrdinalIgnoreCase)
                ? TrafficDirection.Outbound
                : TrafficDirection.Inbound;

            return new TrafficEvent
            {
                Timestamp = DateTime.Parse($"{parts[0]} {parts[1]}"),
                Action = ParseAction(parts[2]),
                Protocol = ParseProtocol(parts[3]),
                SourceAddress = IPAddress.TryParse(parts[4], out var src) ? src : null,
                DestinationAddress = IPAddress.TryParse(parts[5], out var dst) ? dst : null,
                SourcePort = int.TryParse(parts[6], out var sp) ? sp : 0,
                DestinationPort = int.TryParse(parts[7], out var dp) ? dp : 0,
                Direction = direction
            };
        }
        catch
        {
            return null;
        }
    }

    private static TrafficAction ParseAction(string action) => action.ToUpperInvariant() switch
    {
        "ALLOW" => TrafficAction.Allow,
        "DROP" => TrafficAction.Drop,
        "BLOCK" => TrafficAction.Block,
        _ => TrafficAction.Block
    };

    private static TransportProtocol ParseProtocol(string protocol) => protocol.ToUpperInvariant() switch
    {
        "TCP" => TransportProtocol.TCP,
        "UDP" => TransportProtocol.UDP,
        "ICMP" => TransportProtocol.ICMP,
        "ICMPV6" => TransportProtocol.ICMPv6,
        _ => TransportProtocol.Other
    };
}
