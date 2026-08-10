using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinFWManager.Core.Services;

namespace WinFWManager.ViewModels;

/// <summary>One audited block, with the filter id already resolved to a name.</summary>
public class AuditBlockRow
{
    public DateTime Time { get; set; }
    public string Filter { get; set; } = string.Empty;
    public ulong FilterId { get; set; }
    public string? Direction { get; set; }
    public string? Protocol { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string? Application { get; set; }
    public string? Layer { get; set; }
    public int? ProcessId { get; set; }
}

/// <summary>
/// Shows blocks recorded by Windows audit logging.
///
/// This exists because the traffic capture cannot see them. An outbound rule block is
/// refused before a packet is created, so it never reaches the capture at all — the only
/// place it appears is here. Requires block auditing to be on.
/// </summary>
public partial class AuditBlocksViewModel : ObservableObject
{
    private const int ReadLimit = 500;

    public ObservableCollection<AuditBlockRow> Blocks { get; } = new();
    public ICollectionView BlocksView { get; }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isAuditing;
    [ObservableProperty] private string _statusText = "";

    /// <summary>
    /// Free-text filter over application, filter name and endpoints. Not a luxury:
    /// multicast blocks arrive in bursts and buried real blocks 139-to-11 in testing,
    /// so the list is close to unusable without a way to narrow it.
    /// </summary>
    [ObservableProperty] private string _filterText = "";

    public AuditBlocksViewModel()
    {
        BlocksView = CollectionViewSource.GetDefaultView(Blocks);
        BlocksView.Filter = Matches;
    }

    partial void OnFilterTextChanged(string value) => BlocksView.Refresh();

    /// <summary>Called when the tab becomes visible, and by the Refresh button.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var state = WfpAuditPolicy.GetState();
            IsAuditing = state == WfpAuditState.FailureAudit;

            // Reading the Security log and resolving filter ids both take a while, so
            // neither runs on the UI thread.
            var rows = await Task.Run(() =>
                WfpAuditEventReader.ReadRecent(ReadLimit)
                    .Select(b => new AuditBlockRow
                    {
                        Time = b.Time,
                        FilterId = b.FilterId,
                        Filter = WfpFilterResolver.Resolve(b.FilterId) ?? $"filter {b.FilterId}",
                        Direction = WfpAuditEventReader.Humanize(b.Direction),
                        Protocol = WfpAuditEventReader.ProtocolName(b.Protocol),
                        Source = $"{b.SourceAddress}:{b.SourcePort}",
                        Destination = $"{b.DestAddress}:{b.DestPort}",
                        Application = FileName(b.Application),
                        Layer = WfpAuditEventReader.Humanize(b.LayerName),
                        ProcessId = b.ProcessId,
                    })
                    .ToList());

            Blocks.Clear();
            foreach (var row in rows)
                Blocks.Add(row);

            StatusText = state switch
            {
                WfpAuditState.FailureAudit when rows.Count > 0 =>
                    $"{rows.Count} recorded blocks. Newest first.",
                WfpAuditState.FailureAudit =>
                    "Auditing is on, but nothing has been blocked yet.",
                WfpAuditState.Disabled =>
                    "Block auditing is off, so nothing is being recorded. Turn it on from the "
                    + "Traffic Monitor toolbar — it is the only way to see outbound rule blocks, "
                    + "which never reach the traffic capture.",
                _ =>
                    "Cannot read the audit policy — run as Administrator.",
            };
        }
        catch (Exception ex)
        {
            StatusText = $"Could not read the Security log: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Writes the rows currently visible, filters applied, to CSV.</summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var rows = ((System.Collections.IEnumerable)BlocksView).Cast<AuditBlockRow>().ToList();

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export audited blocks",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"winfw-audited-blocks-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));

            writer.WriteLine(Csv.Row(
                "Time", "Filter", "Filter ID", "Direction", "Protocol",
                "Source", "Destination", "Application", "PID", "Layer"));

            foreach (var r in rows)
            {
                writer.WriteLine(Csv.Row(
                    r.Time, r.Filter, r.FilterId, r.Direction, r.Protocol,
                    r.Source, r.Destination, r.Application, r.ProcessId, r.Layer));
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not write the file.\n\n{ex.Message}",
                "Export audited blocks", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private bool Matches(object obj)
    {
        if (obj is not AuditBlockRow row) return false;
        if (string.IsNullOrWhiteSpace(FilterText)) return true;

        var needle = FilterText.Trim();

        return Contains(row.Application, needle)
            || Contains(row.Filter, needle)
            || Contains(row.Source, needle)
            || Contains(row.Destination, needle)
            || Contains(row.Protocol, needle)
            || Contains(row.Direction, needle);

        static bool Contains(string? haystack, string needle)
            => haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Audit events give a device path such as
    /// "\device\harddiskvolume3\windows\system32\curl.exe"; only the file name is useful
    /// in a list, and the full path stays available in the row tooltip.
    /// </summary>
    private static string? FileName(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return devicePath;

        var slash = devicePath.LastIndexOfAny(['\\', '/']);
        return slash >= 0 && slash < devicePath.Length - 1 ? devicePath[(slash + 1)..] : devicePath;
    }
}
