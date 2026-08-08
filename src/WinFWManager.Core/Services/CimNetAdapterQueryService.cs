using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace WinFWManager.Core.Services;

/// <summary>
/// Reads the <c>Hidden</c> flag from <c>MSFT_NetAdapter</c>, the same property
/// <c>Get-NetAdapter</c> filters on unless you pass <c>-IncludeHidden</c>.
///
/// This is deliberately authoritative rather than name-based: a machine running a
/// localized Windows reports connection names in the system language (for example
/// "Anslutning till lokalt nätverk*" on Swedish installs), so matching on adapter
/// names cannot reliably identify pseudo-adapters.
/// </summary>
public sealed class CimNetAdapterQueryService : IDisposable
{
    private const string CimNamespace = @"root\standardcimv2";
    private readonly CimSession _session;

    public CimNetAdapterQueryService()
    {
        // DCOM for local WMI access — matches CimFirewallQueryService, no WinRM needed.
        _session = CimSession.Create("localhost", new DComSessionOptions());
    }

    /// <summary>
    /// Returns the GUIDs of adapters Windows considers real.
    ///
    /// The provider already excludes hidden adapters from this class: on a test machine
    /// it returned 6 instances, exactly matching <c>Get-NetAdapter</c>, where
    /// <c>Get-NetAdapter -IncludeHidden</c> reported 21 and .NET's
    /// <c>GetAllNetworkInterfaces()</c> reported 48. So membership of this set — not the
    /// <c>Hidden</c> property, which is False on every returned instance — is the signal.
    ///
    /// Returns an empty set if CIM is unavailable (broken WMI repository, no rights), so
    /// callers can fall back to <see cref="NetworkInterfaceService.LooksLikePseudoAdapter"/>.
    /// </summary>
    public IReadOnlySet<Guid> GetVisibleAdapterGuids()
    {
        var visible = new HashSet<Guid>();

        try
        {
            var instances = _session.QueryInstances(
                CimNamespace, "WQL",
                "SELECT InterfaceGuid, Hidden FROM MSFT_NetAdapter");

            foreach (var instance in instances)
            {
                using (instance)
                {
                    var rawGuid = instance.CimInstanceProperties["InterfaceGuid"]?.Value as string;
                    if (!Guid.TryParse(rawGuid, out var guid))
                        continue;

                    // Defensive: honour Hidden if a future/other provider does set it.
                    if (instance.CimInstanceProperties["Hidden"]?.Value as bool? == true)
                        continue;

                    visible.Add(guid);
                }
            }
        }
        catch
        {
            // WMI unavailable — signal "no data" so the caller uses the heuristic.
            return new HashSet<Guid>();
        }

        return visible;
    }

    public void Dispose() => _session.Dispose();
}
