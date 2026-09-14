using System.Windows;

namespace Nova4Me2.App.Views;

internal static class Ui
{
    public static MainWindow Main => (MainWindow)Application.Current.MainWindow;
    public static Services.AppState State => Services.AppState.Current;

    public static string? PickFolder(string title, string? initial = null)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public static string? SaveFile(string title, string filter, string defaultName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = defaultName, AddExtension = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public static string? OpenFile(string title, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public static void SaveReport(Core.Reports.Report r, string defaultName)
    {
        var path = SaveFile("Save report", "HTML report (*.html)|*.html|PDF report (*.pdf)|*.pdf|Text report (*.txt)|*.txt", defaultName + ".html");
        if (path == null) return;
        try { Core.Reports.ReportWriter.Save(r, path); Main.Toast("Report saved", path); }
        catch (Exception ex) { Main.Toast("Could not save report", ex.Message, true); }
    }

    public static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }
}
