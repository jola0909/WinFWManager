using System.Diagnostics.Eventing.Reader;

namespace WinFWManager.Core.Services;

/// <summary>A blocked packet or connection as reported by Security auditing.</summary>
public sealed record WfpAuditBlock(
    DateTime Time,
    int EventId,
    ulong FilterId,
    ulong LayerId,
    string? LayerName,
    string? Application,
    string? Direction,
    string? Protocol,
    string? SourceAddress,
    string? SourcePort,
    string? DestAddress,
    string? DestPort,
    int? ProcessId);

/// <summary>
/// Reads Filtering Platform block events from the Security log.
///
/// 5157 is a blocked connection and 5152 a blocked packet. Unlike the ETW capture these
/// carry <c>FilterRTID</c>, the id of the WFP filter that actually made the decision —
/// the one piece of data that turns "the firewall blocked this" into "this rule blocked
/// this". They exist only while failure auditing is enabled; see <see cref="WfpAuditPolicy"/>.
/// </summary>
public static class WfpAuditEventReader
{
    /// <summary>
    /// Most recent block events, newest first. Returns an empty list when the log cannot
    /// be read (needs elevation) or auditing has produced nothing yet.
    /// </summary>
    public static IReadOnlyList<WfpAuditBlock> ReadRecent(int max = 50)
    {
        var results = new List<WfpAuditBlock>();

        try
        {
            // ReverseDirection gives newest-first without reading the whole log.
            var query = new EventLogQuery("Security", PathType.LogName,
                "*[System[(EventID=5152 or EventID=5157)]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);

            for (EventRecord? record = reader.ReadEvent();
                 record != null && results.Count < max;
                 record = reader.ReadEvent())
            {
                using (record)
                {
                    var data = ExtractData(record);

                    results.Add(new WfpAuditBlock(
                        Time: record.TimeCreated ?? DateTime.MinValue,
                        EventId: record.Id,
                        FilterId: ParseId(data, "FilterRTID"),
                        LayerId: ParseId(data, "LayerRTID"),
                        LayerName: Get(data, "LayerName"),
                        Application: Get(data, "Application"),
                        Direction: Get(data, "Direction"),
                        Protocol: Get(data, "Protocol"),
                        SourceAddress: Get(data, "SourceAddress"),
                        SourcePort: Get(data, "SourcePort"),
                        DestAddress: Get(data, "DestAddress"),
                        DestPort: Get(data, "DestPort"),
                        ProcessId: int.TryParse(Get(data, "ProcessId"), out var pid) ? pid : null));
                }
            }
        }
        catch (EventLogNotFoundException)
        {
            return results;
        }
        catch (UnauthorizedAccessException)
        {
            // Reading the Security log needs elevation; surfaced as "no events".
            return results;
        }
        catch (EventLogException)
        {
            return results;
        }

        return results;
    }

    /// <summary>
    /// Pulls the named fields out of the event's XML. The rendered message is localized,
    /// so the structured data is used instead — field names there are stable.
    /// </summary>
    private static Dictionary<string, string> ExtractData(EventRecord record)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var xml = System.Xml.Linq.XDocument.Parse(record.ToXml());
            var ns = xml.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;

            foreach (var element in xml.Descendants(ns + "Data"))
            {
                var name = element.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(name))
                    values[name] = element.Value;
            }
        }
        catch (System.Xml.XmlException)
        {
            // Malformed record — treat as having no data rather than failing the read.
        }

        return values;
    }

    /// <summary>
    /// Security events store direction and layer as message-table references such as
    /// "%%14593" rather than text, and the text they point at is localized. These are
    /// mapped to stable English labels instead.
    ///
    /// The direction values were confirmed against live traffic: outbound TCP from a
    /// blocked process reported %%14593, inbound multicast reported %%14592.
    /// </summary>
    public static string? Humanize(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("%%", StringComparison.Ordinal))
            return value;

        return value switch
        {
            "%%14592" => "Inbound",
            "%%14593" => "Outbound",
            "%%14597" => "Transport layer",
            "%%14608" => "IPsec layer",
            "%%14610" => "ALE Receive/Accept",
            "%%14611" => "ALE Connect",
            _ => value,   // leave unknown codes visible rather than inventing a label
        };
    }

    /// <summary>IANA protocol number to name; the event carries the number.</summary>
    public static string? ProtocolName(string? number) => number switch
    {
        null or "" => null,
        "1" => "ICMP",
        "6" => "TCP",
        "17" => "UDP",
        "58" => "ICMPv6",
        _ => number,
    };

    private static string? Get(Dictionary<string, string> data, string name)
        => data.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    private static ulong ParseId(Dictionary<string, string> data, string name)
        => ulong.TryParse(Get(data, name), out var id) ? id : 0;
}
