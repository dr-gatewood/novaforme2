using System.IO;
using System.Windows;
using System.Windows.Controls;
using Nova4Me2.Core.Analysis;

namespace Nova4Me2.App.Views;

public sealed class FixRow
{
    public RepairKind Kind { get; init; }
    public Finding? Finding { get; init; }
    public string Title => RepairKindText.Title(Kind);
    public string Why => Finding?.Detail ?? "";
    public string How => RepairKindText.Explain(Kind);
    public string Commands { get; init; } = "";
    public bool HasCommands => Commands.Length > 0;
}

public sealed class BackupRow
{
    public string Path { get; init; } = "";
    public string Label => System.IO.Path.GetFileNameWithoutExtension(Path).Replace('-', ' ');
}

public partial class RepairView : UserControl, INovaView
{
    public RepairView()
    {
        InitializeComponent();
        Ui.State.SourceChanged += () => Dispatcher.BeginInvoke(OnShown);
    }

    public void OnShown()
    {
        var h = Ui.State.LastHealth;
        var fixes = new List<FixRow>();
        var advice = new List<FixRow>();
        if (h != null)
        {
            foreach (var f in h.AllFindings.Where(f => f.IsFixableHere)) if (fixes.All(x => x.Kind != f.Repair)) fixes.Add(new FixRow { Kind = f.Repair, Finding = f });
            foreach (var k in h.AllFindings.Where(f => f.Repair != RepairKind.None && !f.IsFixableHere).Select(f => f.Repair).Concat(h.BootFix.Recommended).Distinct())
                if (fixes.All(x => x.Kind != k)) advice.Add(new FixRow { Kind = k, Commands = k == RepairKind.RebuildBcd ? BcdCommands() : "" });
        }
        if (advice.Count == 0) advice.Add(new FixRow { Kind = RepairKind.RebuildBcd, Commands = BcdCommands() });
        FixList.ItemsSource = fixes;
        AdviceList.ItemsSource = advice;
        NoFixText.Text = h == null ? "Run the analysis to see which fixes apply." : fixes.Count == 0 ? "The analysis found nothing that a sector-level repair would fix. See the guidance below and the Health view's assessment." : "";
        NoFixText.Visibility = NoFixText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        LoadBackups();
    }

    private static string BcdCommands()
    {
        return "Boot the Windows installation USB → Repair your computer → Troubleshoot → Command Prompt, then (adjust letters: X: = the Windows volume, S: = the EFI System Partition, use diskpart 'list vol' / 'assign letter=S' to find them):\n" +
               "diskpart\n  list disk\n  select disk N\n  list partition\n  select partition <EFI>\n  assign letter=S\n  exit\n" +
               "bcdboot X:\\Windows /s S: /f UEFI\nbootrec /fixboot\nbootrec /rebuildbcd\n" +
               "If the volume is still RAW at that point, the boot repair cannot work until the NTFS problem is fixed (or the drive is attached natively instead of through USB).";
    }

    private void LoadBackups()
    {
        try
        {
            var dir = RepairEngine.DefaultBackupDir;
            BackupList.ItemsSource = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").OrderByDescending(f => f).Take(20).Select(f => new BackupRow { Path = f }).ToList() : new List<BackupRow>();
        }
        catch { }
    }

    private void Analyze_Click(object sender, RoutedEventArgs e) => Ui.Main.Navigate("Health");

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FixRow row) return;
        var st = Ui.State;
        if (!st.HasDevice) return;
        if (!Ui.Main.ConfirmTyped(row.Title, $"This writes to {st.SourceName}. The original sectors are saved first so the change can be undone from this view.\n\n{row.How}", "FIX")) return;
        RepairResult? res = null;
        await Ui.Main.RunBusy(row.Title, async p =>
        {
            res = await Task.Run(() =>
            {
                using var w = st.OpenWritable();
                var cand = st.SelectedCandidate;
                return row.Kind switch
                {
                    RepairKind.RestoreBootSectorFromBackup => RepairEngine.RestoreBootSectorFromBackup(w, cand!),
                    RepairKind.RestoreBackupBootSectorFromPrimary => RepairEngine.RestoreBackupBootSectorFromPrimary(w, cand!),
                    RepairKind.RestoreGptFromBackup => RepairEngine.RestoreGptFromBackup(w),
                    RepairKind.RestoreMftFromMirror => RepairEngine.RestoreMftFromMirror(w, Core.Ntfs.NtfsVolume.Open(w, cand!)),
                    RepairKind.ConvertDynamicToBasic => RepairEngine.ConvertDynamicToBasic(w),
                    _ => new RepairResult { Success = false, Message = "Not automated." }
                };
            });
        });
        if (res == null) return;
        ResultText.Text = (res.Success ? "✔ " : "✖ ") + res.Message;
        Ui.Main.Toast(res.Success ? "Repair applied" : "Repair failed", res.Message, !res.Success);
        if (res.Success)
        {
            // Re-open so the volume list and analysis reflect the new state.
            string spec = st.SourceSpec, name = st.SourceName; int? num = st.DriveNumber;
            await Ui.Main.RunBusy("Re-reading the drive", async p => await st.OpenAsync(spec, name, num, p));
            st.LastHealth = null;
            OnShown();
            Ui.Main.Navigate("Health");
        }
        else LoadBackups();
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackupRow row) return;
        var st = Ui.State;
        if (!st.HasDevice) { Ui.Main.Toast("Open the drive first", "", true); return; }
        if (!Ui.Main.ConfirmTyped("Restore original sectors", $"Write the sectors saved in {Path.GetFileName(row.Path)} back to {st.SourceName}?", "UNDO")) return;
        RepairResult? res = null;
        await Ui.Main.RunBusy("Restoring sectors", async p => { res = await Task.Run(() => { using var w = st.OpenWritable(); return RepairEngine.Undo(w, row.Path); }); });
        if (res == null) return;
        ResultText.Text = (res.Success ? "✔ " : "✖ ") + res.Message;
        Ui.Main.Toast(res.Success ? "Restored" : "Restore failed", res.Message, !res.Success);
        if (res.Success) { string spec = st.SourceSpec, name = st.SourceName; int? num = st.DriveNumber; await Ui.Main.RunBusy("Re-reading the drive", async p => await st.OpenAsync(spec, name, num, p)); st.LastHealth = null; OnShown(); }
    }
}
