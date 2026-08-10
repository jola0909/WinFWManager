using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinFWManager.Core.Services;

/// <summary>Whether Windows is auditing Filtering Platform drops.</summary>
public enum WfpAuditState
{
    /// <summary>Not auditing — no 5152/5157 events are produced.</summary>
    Disabled,

    /// <summary>Auditing failures, which is what produces blocked-packet events.</summary>
    FailureAudit,

    /// <summary>Could not be determined (needs elevation, or the query failed).</summary>
    Unknown,
}

/// <summary>
/// Reads and changes the Windows audit policy for the Filtering Platform subcategories.
///
/// This is what makes a blocked packet traceable to the filter that dropped it: the WFP
/// filter id appears only in Security events 5152 and 5157, and those are not written
/// unless auditing is on. It is off by default.
///
/// Subcategories are addressed by GUID rather than name because the names are localized
/// — a Swedish install reports "Ignorera paket för filterplattform" — so name lookup
/// fails on any non-English system.
///
/// Only *failure* auditing is ever enabled. Success auditing would log every permitted
/// connection (event 5156) and floods the Security log on a busy machine.
/// </summary>
public static class WfpAuditPolicy
{
    // Filtering Platform Packet Drop
    private static readonly Guid PacketDropSubcategory =
        new("0CCE9225-69AE-11D9-BED3-505054503030");

    // Filtering Platform Connection
    private static readonly Guid ConnectionSubcategory =
        new("0CCE9226-69AE-11D9-BED3-505054503030");

    private const uint PolicyAuditEventSuccess = 0x1;
    private const uint PolicyAuditEventFailure = 0x2;
    private const uint PolicyAuditEventNone = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AuditPolicyInformation
    {
        public Guid AuditSubCategoryGuid;
        public uint AuditingInformation;
        public Guid AuditCategoryGuid;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AuditQuerySystemPolicy(
        Guid[] pSubCategoryGuids, uint dwPolicyCount, out IntPtr ppAuditPolicy);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AuditSetSystemPolicy(
        AuditPolicyInformation[] pAuditPolicy, uint dwPolicyCount);

    [DllImport("advapi32.dll")]
    private static extern void AuditFree(IntPtr buffer);

    /// <summary>
    /// Current state of the two subcategories. Returns <see cref="WfpAuditState.Unknown"/>
    /// rather than guessing when the query cannot be made — the caller must not present a
    /// failed query as "off", since that would invite turning on something already on.
    /// </summary>
    public static WfpAuditState GetState()
    {
        if (!TryEnableSecurityPrivilege())
            return WfpAuditState.Unknown;

        var guids = new[] { PacketDropSubcategory, ConnectionSubcategory };
        if (!AuditQuerySystemPolicy(guids, (uint)guids.Length, out var buffer) || buffer == IntPtr.Zero)
            return WfpAuditState.Unknown;

        try
        {
            var size = Marshal.SizeOf<AuditPolicyInformation>();
            var auditingFailures = false;

            for (var i = 0; i < guids.Length; i++)
            {
                var entry = Marshal.PtrToStructure<AuditPolicyInformation>(buffer + i * size);
                if ((entry.AuditingInformation & PolicyAuditEventFailure) != 0)
                    auditingFailures = true;
            }

            return auditingFailures ? WfpAuditState.FailureAudit : WfpAuditState.Disabled;
        }
        finally
        {
            AuditFree(buffer);
        }
    }

    /// <summary>
    /// Turns failure auditing for both subcategories on or off. This changes a
    /// system-wide security setting, so it must only ever run from an explicit user
    /// action. Requires elevation.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        if (!TryEnableSecurityPrivilege())
            throw new InvalidOperationException(
                "Changing audit policy needs the Manage auditing privilege — run as Administrator.");

        // Failure only; success auditing would log every allowed connection.
        var setting = enabled ? PolicyAuditEventFailure : PolicyAuditEventNone;

        var policies = new[]
        {
            new AuditPolicyInformation
            {
                AuditSubCategoryGuid = PacketDropSubcategory, AuditingInformation = setting,
            },
            new AuditPolicyInformation
            {
                AuditSubCategoryGuid = ConnectionSubcategory, AuditingInformation = setting,
            },
        };

        if (!AuditSetSystemPolicy(policies, (uint)policies.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not change the audit policy.");
    }

    // ---- SeSecurityPrivilege -------------------------------------------------
    // Both querying and setting audit policy need this privilege. An elevated token
    // holds it but leaves it disabled, so it has to be switched on explicitly.

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const string SeSecurityName = "SeSecurityPrivilege";

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, bool disableAllPrivileges, ref TokenPrivileges newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static bool TryEnableSecurityPrivilege()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
                return false;

            try
            {
                if (!LookupPrivilegeValue(null, SeSecurityName, out var luid))
                    return false;

                var privileges = new TokenPrivileges
                {
                    PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled,
                };

                // AdjustTokenPrivileges reports success even when it changed nothing,
                // so the last error has to be checked to know the privilege is held.
                if (!AdjustTokenPrivileges(token, false, ref privileges,
                        (uint)Marshal.SizeOf<TokenPrivileges>(), IntPtr.Zero, IntPtr.Zero))
                    return false;

                return Marshal.GetLastWin32Error() == 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
