using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nova4Me2.App.Services;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;
using Nova4Me2.Mount;

namespace Nova4Me2.App.Views;

public sealed class FileRow
{
    public NtfsEntry Entry { get; init; } = null!;
    public string Name => Entry.Name;
    public string SizeText => Entry.IsDirectory ? "" : Format.Bytes(Entry.Size);
    public string TypeText => Entry.IsDirectory ? (Entry.IsReparsePoint ? Entry.ReparseKind : "Folder") : Entry.Extension.Length > 0 ? Entry.Extension.TrimStart('.').ToUpperInvariant() + " file" : "File";
    public string ModifiedText => Entry.Modified > DateTime.MinValue ? Entry.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
    public string AttrText
    {
        get
        {
            var l = new List<string>();
            if (Entry.IsDeleted) l.Add("deleted");
            if (Entry.IsCompressed) l.Add("compressed");
            if (Entry.IsSparse) l.Add("sparse");
            if (Entry.IsEncrypted) l.Add("encrypted");
            if (Entry.IsReparsePoint) l.Add(Entry.ReparseKind.ToLowerInvariant());
            if (Entry.IsHidden) l.Add("hidden");
            if (Entry.IsSystem) l.Add("system");
            if ((Entry.Attributes & NtfsFileAttributes.ReadOnly) != 0) l.Add("read-only");
            return string.Join(", ", l);
        }
    }
    public Geometry IconData => Icons.Get(Entry.IsDirectory ? "folder" : "file");
    public Brush IconBrush => (Brush)Application.Current.FindResource(Entry.IsDeleted ? "TextMuted" : Entry.IsDirectory ? "Accent" : "TextMuted");
    public Brush NameBrush => (Brush)Application.Current.FindResource(Entry.IsDeleted ? "TextMuted" : "Text");
}

public partial class BrowseView : UserControl, INovaView
{
    private NtfsEntry? _current;
    private List<NtfsEntry> _currentChildren = new();
    private Point _dragStart;
    private bool _dragArmed;
    private int _loadToken;
    private bool _suppressMode;

    public BrowseView()
    {
        InitializeComponent();
        Ui.State.VolumeChanged += () => Dispatcher.BeginInvoke(ResetTree);
        ShowDeleted.IsChecked = Ui.State.Settings.ShowDeletedFiles;
        ShowSystem.IsChecked = Ui.State.Settings.ShowSystemFiles;
        FillLetters();
    }

    public void OnShown()
    {
        if (!Ui.State.HasVolume) { Subtitle.Text = "Open a drive first (Drives view)."; Tree.Items.Clear(); List.ItemsSource = null; return; }
        if (Tree.Items.Count == 0) ResetTree();
        UpdateMountUi();
    }

    private void FillLetters()
    {
        MountLetter.Items.Clear();
        if (!OperatingSystem.IsWindows()) return;
        var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
        foreach (char c in "RSTUVWXYZNOPQMLKJIHGFE") if (!used.Contains(c)) MountLetter.Items.Add($"{c}:");
        string pref = Ui.State.Settings.MountLetter;
        MountLetter.SelectedItem = MountLetter.Items.Cast<string>().FirstOrDefault(s => s == pref) ?? (MountLetter.Items.Count > 0 ? MountLetter.Items[0] : null);
    }

    // ---- tree ----
    private void ResetTree()
    {
        Tree.Items.Clear();
        var st = Ui.State;
        if (!st.HasVolume) return;
        _suppressMode = true;
        ModeMft.IsChecked = st.UseMftScan; ModeIndex.IsChecked = !st.UseMftScan;
        _suppressMode = false;
        Subtitle.Text = $"{st.SelectedCandidate?.Display}  ·  {st.Source!.Name}";
        var root = st.Source.Root;
        var item = MakeNode(root, st.Volume!.Info.Label.Length > 0 ? st.Volume.Info.Label : "Volume");
        Tree.Items.Add(item);
        item.IsExpanded = true;
        item.IsSelected = true;
        _ = LoadFolderAsync(root);
    }

    private TreeViewItem MakeNode(NtfsEntry e, string? label = null)
    {
        var tvi = new TreeViewItem { Header = MakeHeader(label ?? e.Name, e), Tag = e };
        tvi.Items.Add("…"); // placeholder so the expander shows; replaced on expand
        return tvi;
    }

    private StackPanel MakeHeader(string text, NtfsEntry e)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var p = new System.Windows.Shapes.Path { Data = Icons.Get("folder"), Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.5, Margin = new Thickness(0, 0, 6, 0) };
        p.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, e.IsDeleted ? "TextMuted" : "Accent");
        var tb = new TextBlock { Text = text };
        tb.SetResourceReference(TextBlock.ForegroundProperty, e.IsDeleted ? "TextMuted" : "Text");
        sp.Children.Add(p); sp.Children.Add(tb);
        return sp;
    }

    private async void Tree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem tvi || tvi.Tag is not NtfsEntry dir) return;
        if (tvi.Items.Count == 1 && tvi.Items[0] is string)
        {
            tvi.Items.Clear();
            var src = Ui.State.Source;
            if (src == null) return;
            List<NtfsEntry> kids;
            try { kids = await Task.Run(() => src.List(dir)); } catch (Exception ex) { Log.Warn($"List {dir.Path}: {ex.Message}"); return; }
            foreach (var k in kids.Where(k => k.IsDirectory && Visible(k))) tvi.Items.Add(MakeNode(k));
        }
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem tvi && tvi.Tag is NtfsEntry dir && dir != _current) _ = LoadFolderAsync(dir);
    }

    private bool Visible(NtfsEntry e)
    {
        if (e.IsDeleted && ShowDeleted.IsChecked != true) return false;
        if (e.IsMetaFile && ShowSystem.IsChecked != true) return false;
        return true;
    }

    // ---- list ----
    private async Task LoadFolderAsync(NtfsEntry dir)
    {
        var src = Ui.State.Source;
        if (src == null) return;
        int token = ++_loadToken;
        _current = dir;
        StatusLine.Text = "Loading…";
        BuildBreadcrumb(dir);
        List<NtfsEntry> kids;
        try { kids = await Task.Run(() => src.List(dir)); }
        catch (Exception ex) { if (token == _loadToken) { StatusLine.Text = "Cannot list folder: " + ex.Message; List.ItemsSource = null; } return; }
        if (token != _loadToken) return;
        _currentChildren = kids;
        ApplyFilter();
        Animations.FadeIn(List, 180, 4);
    }

    private void ApplyFilter()
    {
        string f = FilterBox.Text.Trim();
        var rows = _currentChildren.Where(Visible).Where(k => f.Length == 0 || k.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).Select(k => new FileRow { Entry = k }).ToList();
        List.ItemsSource = rows;
        long bytes = rows.Where(r => !r.Entry.IsDirectory).Sum(r => r.Entry.Size);
        StatusLine.Text = $"{rows.Count(r => r.Entry.IsDirectory):N0} folders, {rows.Count(r => !r.Entry.IsDirectory):N0} files ({Format.Bytes(bytes)})" + (f.Length > 0 ? $" matching \"{f}\"" : "");
        var probs = Ui.State.Volume?.Problems;
        if (probs is { Count: > 0 }) StatusLine.Text += $"  ·  {probs.Count} volume note(s) in the log";
    }

    private void BuildBreadcrumb(NtfsEntry dir)
    {
        Breadcrumb.Children.Clear();
        var chain = new List<NtfsEntry>();
        var src = Ui.State.Source!;
        var cur = dir;
        int guard = 0;
        while (cur != null && guard++ < 256)
        {
            chain.Insert(0, cur);
            if (cur.Record == NtfsVolume.RootRecord || cur.Parent == cur.Record) break;
            cur = Ui.State.MftIndex != null && Ui.State.MftIndex.ByRecord.TryGetValue(cur.Parent, out var p) ? p : ResolveParent(cur);
        }
        if (chain.Count == 0 || chain[0].Record != NtfsVolume.RootRecord) chain.Insert(0, src.Root);
        foreach (var e in chain.Distinct())
        {
            var b = new Button { Content = e.Record == NtfsVolume.RootRecord ? (Ui.State.Volume?.Info.Label is { Length: > 0 } l ? l : "Volume") : e.Name, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0), Tag = e };
            b.Click += (_, _) => _ = LoadFolderAsync((NtfsEntry)b.Tag);
            Breadcrumb.Children.Add(b);
            Breadcrumb.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0), Foreground = (Brush)FindResource("TextMuted") });
        }
        if (Breadcrumb.Children.Count > 0) Breadcrumb.Children.RemoveAt(Breadcrumb.Children.Count - 1);
    }

    private NtfsEntry? ResolveParent(NtfsEntry e)
    {
        // Walk the path string: cheaper than re-reading records.
        if (e.Path.Length == 0) return null;
        int cut = e.Path.LastIndexOf('\\');
        if (cut < 0) return Ui.State.Source!.Root;
        try { return Ui.State.Volume!.Resolve(e.Path[..cut]); } catch { return null; }
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is FileRow r && r.Entry.IsDirectory) _ = LoadFolderAsync(r.Entry);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is FileRow r && r.Entry.IsDirectory) { _ = LoadFolderAsync(r.Entry); e.Handled = true; }
        else if (e.Key == Key.Back && _current != null && _current.Record != NtfsVolume.RootRecord) { var p = ResolveParent(_current) ?? Ui.State.Source!.Root; _ = LoadFolderAsync(p); e.Handled = true; }
        else if (e.Key == Key.F5 && _current != null) _ = LoadFolderAsync(_current);
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopyDefault_Click(sender, e);
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        Ui.State.Settings.ShowDeletedFiles = ShowDeleted.IsChecked == true;
        Ui.State.Settings.ShowSystemFiles = ShowSystem.IsChecked == true;
        if (ShowDeleted.IsChecked == true && !Ui.State.UseMftScan && Ui.State.HasVolume) { ModeMft.IsChecked = true; return; }
        if (ShowDeleted.IsChecked == true && Ui.State.UseMftScan && Ui.State.MftIndex is { DeletedRecords: > 0 } idx && idx.ByRecord.Values.All(v => !v.IsDeleted)) { _ = SwitchModeAsync(true); return; }
        ApplyFilter();
        ResetTreeKeepFolder();
    }

    private void ResetTreeKeepFolder() { var cur = _current; ResetTree(); if (cur != null) _ = LoadFolderAsync(cur); }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressMode || !IsLoaded || !Ui.State.HasVolume) return;
        _ = SwitchModeAsync(ModeMft.IsChecked == true);
    }

    private async Task SwitchModeAsync(bool mft)
    {
        if (mft == Ui.State.UseMftScan && !(mft && ShowDeleted.IsChecked == true && Ui.State.MftIndex != null && Ui.State.MftIndex.ByRecord.Values.All(v => !v.IsDeleted) && Ui.State.MftIndex.DeletedRecords > 0)) { ResetTree(); return; }
        var cur = _current;
        await Ui.Main.RunBusy(mft ? "Scanning the $MFT" : "Switching to directory index", async p =>
        {
            var prog = new Progress<(long Done, long Total)>(t => ((IProgress<string>)p).Report($"{t.Done:N0} of {t.Total:N0} records"));
            await Ui.State.SetModeAsync(mft, ShowDeleted.IsChecked == true, prog);
        });
        if (mft && Ui.State.MftIndex is { } idx) Ui.Main.Toast("MFT scan complete", $"{idx.InUseRecords:N0} live records, {idx.DeletedRecords:N0} deleted, {idx.Orphans.Count:N0} orphaned entries, {Format.Duration(idx.Elapsed)}.");
        if (cur != null && cur.Record != NtfsVolume.RootRecord)
        {
            // Re-resolve the folder in the new source by path.
            var again = Ui.State.MftIndex != null && Ui.State.MftIndex.ByRecord.TryGetValue(cur.Record, out var e) ? e : Ui.State.Volume?.Resolve(cur.Path);
            if (again != null) _ = LoadFolderAsync(again);
        }
    }

    // ---- selection & copy ----
    private List<NtfsEntry> Selection() => List.SelectedItems.Cast<FileRow>().Select(r => r.Entry).ToList();

    private void CopyTo_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selection();
        if (sel.Count == 0 && _current != null) sel.Add(_current);
        if (sel.Count == 0) return;
        var dest = Ui.PickFolder("Choose the destination folder (on a different, healthy drive)", Ui.State.Settings.DefaultDestination);
        if (dest == null) return;
        Ui.State.Settings.DefaultDestination = dest;
        Enqueue(sel, dest);
    }

    private void CopyDefault_Click(object sender, RoutedEventArgs e)
    {
        var dest = Ui.State.Settings.DefaultDestination;
        if (string.IsNullOrEmpty(dest) || !Directory.Exists(dest)) { CopyTo_Click(sender, e); return; }
        var sel = Selection();
        if (sel.Count == 0) return;
        Enqueue(sel, dest);
    }

    private void Enqueue(List<NtfsEntry> sel, string dest)
    {
        var s = Ui.State.Settings;
        var opt = new CopyOptions
        {
            Collision = Enum.TryParse<CollisionPolicy>(s.CollisionPolicy, out var cp) ? cp : CollisionPolicy.Skip, VerifyAfterCopy = s.VerifyAfterCopy, ZeroFillUnreadable = s.ZeroFillUnreadable,
            CopyAlternateStreams = s.CopyAlternateStreams, PreserveTimestamps = s.PreserveTimestamps, SkipMetaFiles = true
        };
        string name = sel.Count == 1 ? (sel[0].Name.Length > 0 ? sel[0].Name : "Volume root") : $"{sel.Count} items from {(_current?.Path.Length > 0 ? _current.Path : "root")}";
        Ui.State.Jobs.Enqueue(Ui.State.Source!, sel, dest, opt, name);
        Ui.Main.Toast("Recovery job queued", $"{name} → {dest}");
        Ui.Main.Navigate("Recover");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => List_DoubleClick(sender, null!);

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selection();
        if (sel.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(x => "\\" + x.Path)));
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not FileRow r) return;
        var en = r.Entry;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Path: \\{en.Path}");
        sb.AppendLine($"MFT record: {en.Record} (sequence {en.Sequence}), parent {en.Parent}");
        sb.AppendLine($"Size: {en.Size:N0} bytes, allocated {en.AllocatedSize:N0}");
        sb.AppendLine($"Created: {en.Created.ToLocalTime()}\nModified: {en.Modified.ToLocalTime()}\nAccessed: {en.Accessed.ToLocalTime()}");
        sb.AppendLine($"Attributes: {en.Attributes}{(en.ReparseTag != 0 ? $" ({en.ReparseKind})" : "")}");
        try
        {
            var vol = Ui.State.Volume!;
            var rec = vol.GetRecord(en.Record);
            foreach (var a in vol.GetLogicalAttributes(rec)) sb.AppendLine("  " + a);
            var streams = vol.ListStreams(en.Record).Where(s => s.Name.Length > 0).ToList();
            if (streams.Count > 0) sb.AppendLine("Alternate data streams: " + string.Join(", ", streams.Select(s => $"{s.Name} ({Format.Bytes(s.Size)})")));
            if (rec.Problems.Count > 0) sb.AppendLine("Record problems: " + string.Join("; ", rec.Problems));
        }
        catch (Exception ex) { sb.AppendLine("Record unreadable: " + ex.Message); }
        MessageBox.Show(Ui.Main, sb.ToString(), en.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- drag out ----
    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        var item = ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject) as ListViewItem;
        _dragArmed = item != null;
    }

    private void List_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        var sel = Selection();
        if (sel.Count == 0 || Ui.State.Source == null) return;
        VirtualFileDataObject data;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            data = new VirtualFileDataObject(Ui.State.Source, sel);
        }
        catch (Exception ex) { Ui.Main.Toast("Cannot start drag", ex.Message, true); return; }
        finally { Mouse.OverrideCursor = null; }
        if (data.Count >= 50000) Ui.Main.Toast("Large selection", "Only the first 50,000 items are offered to the drop target. Use Copy to… for bigger jobs.");
        StatusLine.Text = $"Dragging {data.Count:N0} item(s), {Format.Bytes(data.TotalBytes)} — Explorer copies them when you drop.";
        try
        {
            bool dropped = NativeDragDrop.Copy(data);
            StatusLine.Text = dropped ? $"Dropped {data.Count:N0} item(s); Explorer is copying them from the drive." : "Drag cancelled.";
        }
        catch (Exception ex) { Log.Warn("Drag failed: " + ex.Message); try { DragDrop.DoDragDrop(List, data, DragDropEffects.Copy); } catch { } }
    }

    // ---- mount ----
    private async void Mount_Click(object sender, RoutedEventArgs e)
    {
        var st = Ui.State;
        if (st.Mount != null)
        {
            await Task.Run(() => { try { st.Mount.Dispose(); } catch { } });
            st.Mount = null;
            UpdateMountUi();
            Ui.Main.Toast("Unmounted", "The virtual drive was removed.");
            return;
        }
        if (!st.HasVolume) return;
        if (!MountSession.IsWinFspInstalled(out var detail))
        {
            if (MessageBox.Show(Ui.Main, detail + "\n\nOpen the WinFsp download page now?", "WinFsp required", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes) Ui.OpenUrl("https://winfsp.dev/rel/");
            return;
        }
        string letter = MountLetter.SelectedItem as string ?? "R:";
        st.Settings.MountLetter = letter;
        try
        {
            var src = st.Source!;
            bool showSys = ShowSystem.IsChecked == true;
            st.Mount = await Task.Run(() => MountSession.Mount(src, letter, showSys));
            UpdateMountUi();
            Ui.Main.Toast("Mounted", $"The volume is now available read-only as drive {letter}. Any program can open files from it.");
            Ui.OpenFolder(letter + "\\");
        }
        catch (Exception ex) { Ui.Main.Toast("Mount failed", ex.GetBaseException().Message, true); }
    }

    private void UpdateMountUi()
    {
        var m = Ui.State.Mount;
        MountButton.Content = m != null ? $"Unmount {m.MountPoint}" : "Mount as drive";
        MountLetter.IsEnabled = m == null;
        MountStatus.Text = m != null ? $"Mounted read-only at {m.MountPoint}" : "";
    }
}
