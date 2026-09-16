using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using Nova4Me2.App.Services;
using Nova4Me2.App.Views;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Util;

namespace Nova4Me2.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _views = new();
    private readonly Dictionary<string, Border> _navButtons = new();
    private string _currentView = "";
    private UIElement? _currentElement;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _deviceDebounce;
    /// <summary>Raised (debounced, on the UI thread) when Windows reports a disk/volume arrival or removal.</summary>
    public event Action<bool>? DeviceChanged;

    private static readonly (string Key, string Title, string Icon)[] Nav =
    {
        ("Drives", "Drives", "drives"), ("Browse", "Browse files", "browse"), ("Recover", "Recover", "recover"), ("Clone", "Clone / Image", "clone"),
        ("Health", "Health", "health"), ("Forensics", "Analysis / Forensics", "search"), ("Repair", "Repair", "repair"), ("Firmware", "Firmware", "firmware"), ("Info", "Drive info", "info"), ("Windows", "Windows tools", "windows"), ("Settings", "Settings", "settings")
    };

    public AppState State => AppState.Current;

    public MainWindow()
    {
        InitializeComponent();
        BuildNav();
        Log.Subscribe(e => Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add($"{e.Time:HH:mm:ss} [{e.Level}] {e.Message}");
            while (LogList.Items.Count > 2000) LogList.Items.RemoveAt(0);
            LogList.ScrollIntoView(LogList.Items[^1]);
            if (e.Level >= LogLevel.Warn) StatusText.Text = e.Message;
        }));
        State.LinkChanged += s => Dispatcher.BeginInvoke(() => UpdateLink(s));
        State.SourceChanged += () => Dispatcher.BeginInvoke(UpdateTitle);
        State.VolumeChanged += () => Dispatcher.BeginInvoke(UpdateTitle);
        State.DeviceMessage += m => Dispatcher.BeginInvoke(() => StatusText.Text = m);
        State.Jobs.JobFinished += j => Dispatcher.BeginInvoke(() => Toast(j.Status == JobStatus.Done ? "Recovery job finished" : "Recovery job " + j.Status.ToString().ToLowerInvariant(), j.StatusText, j.Status == JobStatus.Failed));
        _statusTimer.Tick += (_, _) => UpdateStats();
        _statusTimer.Start();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F1) { ToggleHelp(); e.Handled = true; } else if (e.Key == Key.Escape && HelpFlyout.Visibility == Visibility.Visible) { HideHelp(); e.Handled = true; } };
        if (State.Settings.ShowLogPanel) LogRow.Height = new GridLength(140);
        Loaded += (_, _) => { Navigate("Drives"); _ = RefreshSelectorAsync(); };
        DeviceChanged += arrival => _ = RefreshSelectorAsync();
        State.SourceChanged += () => Dispatcher.BeginInvoke(() => _ = RefreshSelectorAsync());
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/nova4me2.ico")); } catch { }
        Closing += (_, e) =>
        {
            if (State.Jobs.AnyActive && MessageBox.Show(this, "A recovery job is still running. Quit anyway?", "Nova4Me2", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            State.Settings.Save();
        };
    }

    private const int WM_DEVICECHANGE = 0x0219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            int ev = wParam.ToInt32();
            if (ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE)
            {
                bool arrival = ev == DBT_DEVICEARRIVAL;
                _deviceDebounce?.Stop();
                _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _deviceDebounce.Tick += (_, _) => { _deviceDebounce!.Stop(); DeviceChanged?.Invoke(arrival); };
                _deviceDebounce.Start();
            }
        }
        return IntPtr.Zero; // never block the message loop; all reactions are async
    }

    private void BuildNav()
    {
        foreach (var (key, title, icon) in Nav)
        {
            var bd = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 2, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var path = new Path { Data = Icons.Get(icon), Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            path.SetResourceReference(Shape.StrokeProperty, "TextMuted");
            var tb = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            sp.Children.Add(path);
            sp.Children.Add(tb);
            bd.Child = sp;
            bd.MouseLeftButtonUp += (_, _) => Navigate(key);
            bd.MouseEnter += (_, _) => { if (_currentView != key) bd.SetResourceReference(Border.BackgroundProperty, "PanelAlt"); };
            bd.MouseLeave += (_, _) => { if (_currentView != key) bd.Background = Brushes.Transparent; };
            NavPanel.Children.Add(bd);
            _navButtons[key] = bd;
        }
    }

    public string CurrentView => _currentView;

    // ---- title-bar drive selector (available on every view) ----
    private sealed class DriveOption
    {
        public string Label = "";
        public int Number = -1;
        public string Spec = "";
        public bool IsImage;
        public bool IsPlaceholder;
        public override string ToString() => Label;
    }
    private bool _selectorBusy;

    private async Task RefreshSelectorAsync()
    {
        if (_selectorBusy) return;
        _selectorBusy = true;
        try
        {
            var items = new List<DriveOption> { new() { Label = "Select a drive…", IsPlaceholder = true } };
            if (OperatingSystem.IsWindows())
            {
                var list = await Task.Run(() => Core.Devices.Windows.DriveEnumerator.QuickList());
                foreach (var d in list)
                {
                    string label = d.OpenError != null ? $"[{d.Number}] {(d.ProbeTimedOut ? "not responding" : d.OpenError)}"
                        : $"[{d.Number}] {d.Model} · {Core.Util.Format.Bytes(d.Length)}{(d.Attributes.Offline ? " · OFFLINE" : "")}{(d.Storage.IsUsb ? " · USB" : "")}";
                    items.Add(new DriveOption { Label = label, Number = d.Number, Spec = d.DevicePath });
                }
            }
            if (State.HasDevice && State.DriveNumber == null) items.Add(new DriveOption { Label = "Image: " + State.SourceName, Spec = State.SourceSpec });
            items.Add(new DriveOption { Label = "Open image file…", IsImage = true });
            DriveSelector.SelectionChanged -= DriveSelector_SelectionChanged;
            DriveSelector.ItemsSource = items;
            DriveSelector.SelectedItem = items.FirstOrDefault(i => State.HasDevice && (State.DriveNumber is { } n ? i.Number == n : i.Spec == State.SourceSpec)) ?? items[0];
            DriveSelector.SelectionChanged += DriveSelector_SelectionChanged;
        }
        finally { _selectorBusy = false; }
    }

    private void DriveSelector_DropDownOpened(object sender, EventArgs e) => _ = RefreshSelectorAsync();

    private async void DriveSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveSelector.SelectedItem is not DriveOption o || o.IsPlaceholder) return;
        if (o.IsImage)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open disk or partition image", Filter = "Disk images (*.img;*.dd;*.raw;*.bin;*.001)|*.img;*.dd;*.raw;*.bin;*.001|All files (*.*)|*.*" };
            if (dlg.ShowDialog() != true) { await RefreshSelectorAsync(); return; }
            await RunBusy("Opening " + System.IO.Path.GetFileName(dlg.FileName), async p => await State.OpenAsync(dlg.FileName, System.IO.Path.GetFileName(dlg.FileName), null, p));
            await RefreshSelectorAsync();
            return;
        }
        if (o.Number >= 0 && o.Number != State.DriveNumber)
        {
            await RunBusy("Opening " + o.Label, async p => await State.OpenAsync(o.Spec, o.Label.Split(" · ")[0], o.Number, p));
            await RefreshSelectorAsync();
        }
    }

    public void Navigate(string key)
    {
        if (!_views.TryGetValue(key, out var view))
        {
            view = key switch
            {
                "Drives" => new DrivesView(), "Browse" => new BrowseView(), "Recover" => new RecoverView(), "Clone" => new CloneView(), "Health" => new HealthView(),
                "Repair" => new RepairView(), "Forensics" => new ForensicsView(), "Firmware" => new FirmwareView(), "Info" => new InfoView(), "Windows" => new WindowsView(), "Settings" => new SettingsView(), _ => new DrivesView()
            };
            _views[key] = view;
        }
        foreach (var (k, bd) in _navButtons)
        {
            bool sel = k == key;
            if (sel) bd.SetResourceReference(Border.BackgroundProperty, "NavSelected"); else bd.Background = Brushes.Transparent;
            var sp = (StackPanel)bd.Child;
            ((Path)sp.Children[0]).SetResourceReference(Shape.StrokeProperty, sel ? "Accent" : "TextMuted");
            ((TextBlock)sp.Children[1]).SetResourceReference(TextBlock.ForegroundProperty, sel ? "Text" : "TextMuted");
            ((TextBlock)sp.Children[1]).FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (_currentView == key) { (view as INovaView)?.OnShown(); return; }
        _currentView = key;
        var old = _currentElement;
        void Show()
        {
            ContentHost.Children.Clear();
            ContentHost.Children.Add(view);
            _currentElement = view;
            Animations.FadeIn(view, 240, 10);
            (view as INovaView)?.OnShown();
            if (HelpFlyout.Visibility == Visibility.Visible) RenderHelp(key);
        }
        if (old != null) Animations.FadeOut(old, 110, Show); else Show();
    }

    private void UpdateTitle()
    {
        TitleSource.Text = State.HasDevice ? State.SourceName : "No drive opened";
        var c = State.SelectedCandidate;
        TitleVolume.Text = State.HasDevice
            ? (c != null ? $"{c.Display}{(State.UseMftScan ? "  ·  rebuilt from MFT" : "")}" : $"{State.Volumes.Count} NTFS volume(s) found")
            : "Pick a drive or open an image file to begin";
    }

    private void UpdateLink(ConnectionState s)
    {
        string key = s switch { ConnectionState.Connected => "Good", ConnectionState.Degraded => "Warn", ConnectionState.Reconnecting => "Warn", ConnectionState.Lost => "Bad", _ => "TextMuted" };
        LinkDot.SetResourceReference(Shape.FillProperty, key);
        LinkText.Text = s switch { ConnectionState.Connected => "Connected", ConnectionState.Degraded => "Degraded", ConnectionState.Reconnecting => "Reconnecting…", ConnectionState.Lost => "Link lost", _ => "No device" };
        if (s == ConnectionState.Reconnecting) Animations.Pulse(LinkDot); else Animations.StopPulse(LinkDot);
    }

    private void UpdateStats()
    {
        var d = State.Device;
        if (d == null) { StatusStats.Text = ""; return; }
        var st = d.Stats;
        StatusStats.Text = $"{Format.Bytes(st.BytesRead)} read · {st.ReadErrors} errors · {st.Reconnects} reconnects · {st.BadSectors} bad sectors";
    }

    // ---- help ----
    private void HelpButton_Click(object sender, RoutedEventArgs e) => ToggleHelp();
    private void HelpClose_Click(object sender, RoutedEventArgs e) => HideHelp();

    private void ToggleHelp()
    {
        if (HelpFlyout.Visibility == Visibility.Visible) HideHelp();
        else { RenderHelp(_currentView); HelpFlyout.Visibility = Visibility.Visible; Animations.SlideIn(HelpFlyout, 60); }
    }

    private void HideHelp() => Animations.SlideOut(HelpFlyout, 60, 180, () => HelpFlyout.Visibility = Visibility.Collapsed);

    private void RenderHelp(string view)
    {
        HelpTitle.Text = "Help — " + (Nav.FirstOrDefault(n => n.Key == view).Title ?? view);
        HelpBody.Children.Clear();
        foreach (var raw in HelpContent.For(view).Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) { HelpBody.Children.Add(new Border { Height = 8 }); continue; }
            if (line.StartsWith("# ")) { HelpBody.Children.Add(new TextBlock { Text = line[2..], Style = (Style)FindResource("H2"), Margin = new Thickness(0, 10, 0, 6) }); continue; }
            if (line.StartsWith("- "))
            {
                var g = new Grid { Margin = new Thickness(4, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var dot = new TextBlock { Text = "•" };
                dot.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                var tb = new TextBlock { Text = line[2..], TextWrapping = TextWrapping.Wrap };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "Text");
                Grid.SetColumn(tb, 1);
                g.Children.Add(dot); g.Children.Add(tb);
                HelpBody.Children.Add(g);
                continue;
            }
            var p = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            p.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            HelpBody.Children.Add(p);
        }
    }

    // ---- toasts / busy ----
    public void Toast(string title, string message, bool isError = false)
    {
        ToastTitle.Text = title;
        ToastText.Text = message;
        ToastBox.SetResourceReference(Border.BorderBrushProperty, isError ? "Bad" : "Accent");
        ToastBox.Visibility = Visibility.Visible;
        Animations.FadeIn(ToastBox, 200, 12);
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(isError ? 9 : 5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Animations.FadeOut(ToastBox, 250, () => ToastBox.Visibility = Visibility.Collapsed); };
        _toastTimer.Start();
        StatusText.Text = title + (message.Length > 0 ? " — " + message : "");
    }

    public async Task RunBusy(string title, Func<IProgress<string>, Task> work)
    {
        BusyText.Text = title;
        BusyDetail.Text = "";
        BusyOverlay.Visibility = Visibility.Visible;
        Animations.FadeIn(BusyOverlay, 150, 0);
        var progress = new Progress<string>(m => BusyDetail.Text = m);
        try { await work(progress); }
        catch (Exception ex) { Log.Error(title + ": " + ex.Message); Toast(title + " failed", ex.GetBaseException().Message, true); }
        finally { BusyOverlay.Visibility = Visibility.Collapsed; }
    }

    public bool ConfirmTyped(string title, string message, string word)
    {
        var dlg = new Dialogs.ConfirmDialog(title, message, word) { Owner = this };
        return dlg.ShowDialog() == true;
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        bool show = LogRow.Height.Value == 0;
        LogRow.Height = new GridLength(show ? 140 : 0);
        State.Settings.ShowLogPanel = show;
    }
}

public interface INovaView
{
    void OnShown();
}
