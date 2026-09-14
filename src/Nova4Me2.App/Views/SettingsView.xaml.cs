using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nova4Me2.App.Services;

namespace Nova4Me2.App.Views;

public partial class SettingsView : UserControl, INovaView
{
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        BuildThemes();
        Load();
        AboutText.Text = $"Nova4Me2 Drive Recovery {typeof(SettingsView).Assembly.GetName().Version?.ToString(3)} — reads NTFS directly from raw sectors, keeps flaky USB links alive, images failing drives, and explains what is wrong. Settings and logs live in {Settings.Dir}.";
    }

    public void OnShown() => Load();

    private void BuildThemes()
    {
        ThemePanel.Children.Clear();
        foreach (var (key, name, blurb) in ThemeManager.Themes)
        {
            var dict = new ResourceDictionary { Source = new Uri($"Themes/{key}.xaml", UriKind.Relative) };
            var card = new Border { Width = 180, Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Cursor = Cursors.Hand, Padding = new Thickness(10), Tag = key, Background = (Brush)dict["Bg"], BorderBrush = key == ThemeManager.Current ? (Brush)dict["Accent"] : (Brush)dict["Border"] };
            var sp = new StackPanel();
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            foreach (var k in new[] { "Panel", "Accent", "Good", "Warn", "Bad" }) swatches.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = (Brush)dict[k], Margin = new Thickness(0, 0, 4, 0) });
            sp.Children.Add(swatches);
            sp.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Foreground = (Brush)dict["Text"] });
            sp.Children.Add(new TextBlock { Text = blurb, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)dict["TextMuted"] });
            card.Child = sp;
            card.MouseLeftButtonUp += (_, _) => { ThemeManager.Apply(key); Ui.State.Settings.Theme = key; Ui.State.Settings.Save(); BuildThemes(); };
            ThemePanel.Children.Add(card);
        }
    }

    private void Load()
    {
        _loading = true;
        var s = Ui.State.Settings;
        ReconnectBox.Text = s.ReconnectTimeoutSeconds.ToString();
        ChunkBox.Text = s.ChunkKiB.ToString();
        ThrottleBox.Text = s.ThrottleMBps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        KeepaliveBox.IsChecked = s.Keepalive;
        IoTimeoutBox.Text = s.IoTimeoutSeconds.ToString();
        GuardBox.IsChecked = !s.MountGuardDismissed;
        DestBox.Text = s.DefaultDestination;
        ColSkip.IsChecked = s.CollisionPolicy == "Skip"; ColOverwrite.IsChecked = s.CollisionPolicy == "Overwrite"; ColRename.IsChecked = s.CollisionPolicy == "Rename";
        VerifyBox.IsChecked = s.VerifyAfterCopy; ZeroFillBox.IsChecked = s.ZeroFillUnreadable; AdsBox.IsChecked = s.CopyAlternateStreams; TimesBox.IsChecked = s.PreserveTimestamps;
        _loading = false;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = Ui.State.Settings;
        if (int.TryParse(ReconnectBox.Text, out int r)) s.ReconnectTimeoutSeconds = Math.Clamp(r, 5, 3600);
        if (int.TryParse(ChunkBox.Text, out int c)) s.ChunkKiB = Math.Clamp(c, 64, 16384);
        if (double.TryParse(ThrottleBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t)) s.ThrottleMBps = Math.Max(0, t);
        s.Keepalive = KeepaliveBox.IsChecked == true;
        if (int.TryParse(IoTimeoutBox.Text, out int io)) s.IoTimeoutSeconds = Math.Clamp(io, 5, 300);
        s.MountGuardDismissed = GuardBox.IsChecked != true;
        s.DefaultDestination = DestBox.Text.Trim();
        s.CollisionPolicy = ColOverwrite.IsChecked == true ? "Overwrite" : ColRename.IsChecked == true ? "Rename" : "Skip";
        s.VerifyAfterCopy = VerifyBox.IsChecked == true; s.ZeroFillUnreadable = ZeroFillBox.IsChecked == true; s.CopyAlternateStreams = AdsBox.IsChecked == true; s.PreserveTimestamps = TimesBox.IsChecked == true;
        s.Save();
    }

    private void BrowseDest_Click(object sender, RoutedEventArgs e)
    {
        var d = Ui.PickFolder("Default destination for recovered files", DestBox.Text);
        if (d != null) { DestBox.Text = d; Save(sender, e); }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e) { System.IO.Directory.CreateDirectory(Settings.Dir); Ui.OpenFolder(Settings.Dir); }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var fresh = new Settings();
        var s = Ui.State.Settings;
        s.ReconnectTimeoutSeconds = fresh.ReconnectTimeoutSeconds; s.ChunkKiB = fresh.ChunkKiB; s.ThrottleMBps = fresh.ThrottleMBps; s.Keepalive = fresh.Keepalive; s.IoTimeoutSeconds = fresh.IoTimeoutSeconds; s.MountGuardDismissed = false; s.DefaultDestination = ""; s.CollisionPolicy = fresh.CollisionPolicy;
        s.VerifyAfterCopy = fresh.VerifyAfterCopy; s.ZeroFillUnreadable = fresh.ZeroFillUnreadable; s.CopyAlternateStreams = fresh.CopyAlternateStreams; s.PreserveTimestamps = fresh.PreserveTimestamps; s.Theme = fresh.Theme;
        ThemeManager.Apply(s.Theme);
        s.Save();
        BuildThemes();
        Load();
    }
}
