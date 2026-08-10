using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WinFWManager.Core.Services;

/// <summary>
/// Turns a WFP filter id from a Security audit event into the filter's name.
///
/// This is the step that makes auditing worth enabling. An audit event says filter
/// 72998 blocked a packet; this asks the filtering engine what 72998 is, and for filters
/// created from firewall rules the name is the rule's own name — which is the answer
/// someone actually needs in order to change something.
/// </summary>
public static class WfpFilterResolver
{
    // Filter ids are stable while a filter exists and each is looked up repeatedly
    // across events, so resolved names are cached for the process lifetime.
    private static readonly ConcurrentDictionary<ulong, string?> Cache = new();

    private const uint RpcCAuthnWinNt = 10;

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmDisplayData0
    {
        public IntPtr Name;
        public IntPtr Description;
    }

    /// <summary>
    /// The leading fields of FWPM_FILTER0. Only the display data is read, and it sits
    /// immediately after the key, so the rest of the (large) structure is not declared.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilter0Header
    {
        public Guid FilterKey;
        public FwpmDisplayData0 DisplayData;
    }

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    private static extern uint FwpmEngineOpen0(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    private static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    private static extern uint FwpmFilterGetById0(IntPtr engineHandle, ulong id, out IntPtr filter);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    private static extern void FwpmFreeMemory0(ref IntPtr p);

    /// <summary>
    /// Resolves a filter id to its name, or null when the id is unknown — filters are
    /// transient, so an id from an older event may no longer exist.
    /// </summary>
    public static string? Resolve(ulong filterId)
    {
        if (filterId == 0)
            return null;

        return Cache.GetOrAdd(filterId, static id =>
        {
            IntPtr engine = IntPtr.Zero;
            IntPtr filter = IntPtr.Zero;

            try
            {
                if (FwpmEngineOpen0(null, RpcCAuthnWinNt, IntPtr.Zero, IntPtr.Zero, out engine) != 0)
                    return null;

                if (FwpmFilterGetById0(engine, id, out filter) != 0 || filter == IntPtr.Zero)
                    return null;

                var header = Marshal.PtrToStructure<FwpmFilter0Header>(filter);
                var name = header.DisplayData.Name != IntPtr.Zero
                    ? Marshal.PtrToStringUni(header.DisplayData.Name)
                    : null;

                // Windows pads rule-derived filter names with a leading space.
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                if (filter != IntPtr.Zero) FwpmFreeMemory0(ref filter);
                if (engine != IntPtr.Zero) FwpmEngineClose0(engine);
            }
        });
    }
}
