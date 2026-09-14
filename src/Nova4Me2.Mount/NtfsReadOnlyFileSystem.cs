using System.Runtime.InteropServices;
using Fsp;
using Fsp.Interop;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;
using FileInfo = Fsp.Interop.FileInfo;

namespace Nova4Me2.Mount;

/// <summary>
/// Presents a Nova4Me2 NTFS volume to Windows as a read-only drive letter through WinFsp (user-mode file system).
/// Windows never talks to the damaged volume itself; every request is answered from our own NTFS reader.
/// </summary>
public sealed class NtfsReadOnlyFileSystem : FileSystemBase
{
    private readonly IDirectorySource _src;
    private readonly NtfsVolume _vol;
    private readonly Dictionary<string, NtfsEntry> _pathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, List<NtfsEntry>> _dirCache = new();
    private readonly object _lock = new();
    private static readonly byte[] DefaultSd = SdBytes("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)");
    public bool ShowSystemFiles { get; set; }
    public long Reads, BytesServed;
    public string LastError = "";

    private static byte[] SdBytes(string sddl) { var sd = new System.Security.AccessControl.RawSecurityDescriptor(sddl); var b = new byte[sd.BinaryLength]; sd.GetBinaryForm(b, 0); return b; }

    public NtfsReadOnlyFileSystem(IDirectorySource source)
    {
        _src = source;
        _vol = source.Volume;
    }

    private sealed class Node
    {
        public NtfsEntry Entry = null!;
        public NtfsStream? Stream;
        public string Path = "";
    }

    private List<NtfsEntry> Children(NtfsEntry dir)
    {
        lock (_lock)
        {
            if (_dirCache.TryGetValue(dir.Record, out var l)) return l;
            try { l = _src.List(dir).Where(e => ShowSystemFiles || !e.IsMetaFile).Where(e => !e.IsDeleted).ToList(); }
            catch (Exception ex) { LastError = ex.Message; Log.Warn($"mount: listing {dir.Path} failed: {ex.Message}"); l = new List<NtfsEntry>(); }
            _dirCache[dir.Record] = l;
            return l;
        }
    }

    private NtfsEntry? Resolve(string fileName)
    {
        string p = fileName.Trim('\\');
        if (p.Length == 0) return _src.Root;
        lock (_lock) if (_pathCache.TryGetValue(p, out var hit)) return hit;
        var cur = _src.Root;
        foreach (var part in p.Split('\\'))
        {
            if (!cur.IsDirectory) return null;
            var next = Children(cur).FirstOrDefault(e => string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null) return null;
            cur = next;
        }
        lock (_lock) _pathCache[p] = cur;
        return cur;
    }

    private static ulong Ft(DateTime t) { try { return t <= DateTime.MinValue ? 0 : (ulong)t.ToFileTimeUtc(); } catch { return 0; } }

    private void Fill(NtfsEntry e, out FileInfo fi)
    {
        uint attrs = (uint)e.Attributes & 0x37A7; // keep the standard FILE_ATTRIBUTE_* bits, drop reparse (we do not expose reparse data)
        if (e.IsDirectory) attrs |= 0x10; else if (attrs == 0) attrs = 0x80;
        attrs |= 0x1; // read-only view
        fi = new FileInfo
        {
            FileAttributes = attrs, ReparseTag = 0, FileSize = e.IsDirectory ? 0 : (ulong)Math.Max(0, e.Size), AllocationSize = e.IsDirectory ? 0 : (ulong)Bin.AlignUp(Math.Max(e.Size, e.AllocatedSize), _vol.ClusterSize),
            CreationTime = Ft(e.Created), LastAccessTime = Ft(e.Accessed), LastWriteTime = Ft(e.Modified), ChangeTime = Ft(e.MftModified != DateTime.MinValue ? e.MftModified : e.Modified),
            IndexNumber = (ulong)Math.Max(0, e.Record), HardLinks = 1
        };
    }

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = (ushort)_vol.Boot.BytesPerSector;
        host.SectorsPerAllocationUnit = (ushort)Math.Max(1, _vol.ClusterSize / _vol.Boot.BytesPerSector);
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = uint.MaxValue; // read-only: metadata never changes
        host.VolumeInfoTimeout = uint.MaxValue;
        host.DirInfoTimeout = uint.MaxValue;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.ReparsePoints = false;
        host.NamedStreams = false;
        host.PostCleanupWhenModifiedOnly = true;
        host.PassQueryDirectoryPattern = false;
        host.FlushAndPurgeOnCleanup = false;
        host.VolumeCreationTime = Ft(DateTime.UtcNow);
        host.VolumeSerialNumber = (uint)(_vol.Boot.VolumeSerial & 0xFFFFFFFF);
        host.FileSystemName = "NTFS";
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = new VolumeInfo { TotalSize = (ulong)_vol.Length, FreeSize = 0 };
        volumeInfo.SetVolumeLabel(_vol.Info.Label.Length > 0 ? _vol.Info.Label : "Nova4Me2 Recovery");
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var e = Resolve(fileName);
        if (e == null) { fileAttributes = 0; return STATUS_OBJECT_NAME_NOT_FOUND; }
        Fill(e, out var fi);
        fileAttributes = fi.FileAttributes;
        if (securityDescriptor != null) securityDescriptor = DefaultSd;
        return STATUS_SUCCESS;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!; fileDesc = null!; fileInfo = default; normalizedName = null!;
        var e = Resolve(fileName);
        if (e == null) return STATUS_OBJECT_NAME_NOT_FOUND;
        if ((createOptions & FILE_DIRECTORY_FILE) != 0 && !e.IsDirectory) return STATUS_NOT_A_DIRECTORY;
        if ((createOptions & FILE_NON_DIRECTORY_FILE) != 0 && e.IsDirectory) return STATUS_FILE_IS_A_DIRECTORY;
        const uint writeAccess = 0x0002 | 0x0004 | 0x0100 | 0x00010000 /* FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_ATTRIBUTES | DELETE */;
        if ((grantedAccess & writeAccess) != 0 && !e.IsDirectory) return STATUS_MEDIA_WRITE_PROTECTED;
        var node = new Node { Entry = e, Path = fileName };
        fileNode = node;
        Fill(e, out fileInfo);
        normalizedName = NormalizedPath(fileName);
        return STATUS_SUCCESS;
    }

    private string NormalizedPath(string fileName)
    {
        string p = fileName.Trim('\\');
        if (p.Length == 0) return "\\";
        var parts = new List<string>();
        var cur = _src.Root;
        foreach (var part in p.Split('\\'))
        {
            var next = Children(cur).FirstOrDefault(x => string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null) { parts.Add(part); continue; }
            parts.Add(next.Name);
            cur = next;
        }
        return "\\" + string.Join("\\", parts);
    }

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileNode is Node n) { lock (n) { n.Stream?.Dispose(); n.Stream = null; } }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        Fill(((Node)fileNode).Entry, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var n = (Node)fileNode;
        if (n.Entry.IsDirectory) return STATUS_FILE_IS_A_DIRECTORY;
        try
        {
            lock (n)
            {
                n.Stream ??= _vol.OpenFile(n.Entry);
                if ((long)offset >= n.Stream.Length) return STATUS_END_OF_FILE;
                int want = (int)Math.Min(length, (ulong)(n.Stream.Length - (long)offset));
                var tmp = new byte[want];
                n.Stream.Position = (long)offset;
                int got = 0;
                while (got < want) { int r = n.Stream.Read(tmp, got, want - got); if (r <= 0) break; got += r; }
                Marshal.Copy(tmp, 0, buffer, got);
                bytesTransferred = (uint)got;
                Interlocked.Increment(ref Reads);
                Interlocked.Add(ref BytesServed, got);
                return got == 0 ? STATUS_END_OF_FILE : STATUS_SUCCESS;
            }
        }
        catch (Core.Devices.DeviceLostException ex) { LastError = ex.Message; return STATUS_DEVICE_NOT_CONNECTED; }
        catch (Core.Devices.BadSectorException ex) { LastError = ex.Message; return STATUS_IO_DEVICE_ERROR; }
        catch (Exception ex) { LastError = ex.Message; Log.Warn($"mount: read {n.Path} @ {offset}: {ex.Message}"); return STATUS_UNEXPECTED_IO_ERROR; }
    }

    private sealed class DirEnum
    {
        public List<(string Name, NtfsEntry Entry)> Items = new();
        public int Index;
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker, ref object context, out string fileName, out FileInfo fileInfo)
    {
        var n = (Node)fileNode;
        if (context is not DirEnum de)
        {
            de = new DirEnum();
            if (n.Entry.Record != NtfsVolume.RootRecord)
            {
                de.Items.Add((".", n.Entry));
                var parentPath = n.Path.TrimEnd('\\');
                int cut = parentPath.LastIndexOf('\\');
                var parent = Resolve(cut <= 0 ? "\\" : parentPath[..cut]) ?? _src.Root;
                de.Items.Add(("..", parent));
            }
            foreach (var c in Children(n.Entry).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) de.Items.Add((c.Name, c));
            if (marker != null)
            {
                int idx = de.Items.FindIndex(i => string.Equals(i.Name, marker, StringComparison.OrdinalIgnoreCase));
                de.Index = idx >= 0 ? idx + 1 : 0;
            }
            context = de;
        }
        if (de.Index < de.Items.Count)
        {
            var (name, e) = de.Items[de.Index++];
            fileName = name;
            Fill(e, out fileInfo);
            return true;
        }
        fileName = null!;
        fileInfo = default;
        return false;
    }

    public override int GetDirInfoByName(object fileNode, object fileDesc, string fileName, out string normalizedName, out FileInfo fileInfo)
    {
        var n = (Node)fileNode;
        var c = Children(n.Entry).FirstOrDefault(x => string.Equals(x.Name, fileName, StringComparison.OrdinalIgnoreCase));
        if (c == null) { normalizedName = null!; fileInfo = default; return STATUS_OBJECT_NAME_NOT_FOUND; }
        normalizedName = c.Name;
        Fill(c, out fileInfo);
        return STATUS_SUCCESS;
    }

    // Everything that would modify the volume is refused.
    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    { fileNode = null!; fileDesc = null!; fileInfo = default; normalizedName = null!; return STATUS_MEDIA_WRITE_PROTECTED; }
    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out FileInfo fileInfo) { fileInfo = default; return STATUS_MEDIA_WRITE_PROTECTED; }
    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo) { bytesTransferred = 0; fileInfo = default; return STATUS_MEDIA_WRITE_PROTECTED; }
    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo) { fileInfo = default; return STATUS_MEDIA_WRITE_PROTECTED; }
    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo) { fileInfo = default; return STATUS_MEDIA_WRITE_PROTECTED; }
    public override int CanDelete(object fileNode, object fileDesc, string fileName) => STATUS_MEDIA_WRITE_PROTECTED;
    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) => STATUS_MEDIA_WRITE_PROTECTED;
    public override int SetSecurity(object fileNode, object fileDesc, System.Security.AccessControl.AccessControlSections sections, byte[] securityDescriptor) => STATUS_MEDIA_WRITE_PROTECTED;
    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor) { securityDescriptor = DefaultSd; return STATUS_SUCCESS; }
}
