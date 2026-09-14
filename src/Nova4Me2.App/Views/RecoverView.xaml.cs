using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Nova4Me2.App.Services;

namespace Nova4Me2.App.Views;

public sealed class BoolToVis : IValueConverter
{
    public static readonly BoolToVis Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class CountToVis : IValueConverter
{
    public static readonly CountToVis Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public partial class RecoverView : UserControl, INovaView
{
    public RecoverView()
    {
        InitializeComponent();
        JobList.ItemsSource = Ui.State.Jobs.Jobs;
        Ui.State.Jobs.Jobs.CollectionChanged += (_, _) => UpdateEmpty();
    }

    public void OnShown() => UpdateEmpty();

    private void UpdateEmpty() => EmptyText.Visibility = Ui.State.Jobs.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static CopyJob? JobOf(object sender) => (sender as FrameworkElement)?.DataContext as CopyJob;

    private void Cancel_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.Cts.Cancel();

    private void Open_Click(object sender, RoutedEventArgs e) { if (JobOf(sender) is { } j) Ui.OpenFolder(j.Destination); }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is not { Report: { } r } j) return;
        Ui.SaveReport(r, $"Nova4Me2-recovery-{j.Created:yyyyMMdd-HHmm}");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var jobs = Ui.State.Jobs.Jobs;
        for (int i = jobs.Count - 1; i >= 0; i--) if (jobs[i].IsFinished) jobs.RemoveAt(i);
    }
}
