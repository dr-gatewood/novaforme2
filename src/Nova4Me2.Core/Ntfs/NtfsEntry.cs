namespace Nova4Me2.Core.Ntfs;

/// <summary>A file or directory as presented to the UI / CLI / mount layer.</summary>
public sealed class NtfsEntry
{
    public long Record { get; init; }
    public ushort Sequence { get; init; }
    public long Parent { get; init; }
    public string Name { get; init; } = "";
    public byte Namespace { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public long AllocatedSize { get; init; }
    public DateTime Created { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Accessed { get; init; }
    public DateTime MftModified { get; init; }
    public NtfsFileAttributes Attributes { get; init; }
    public uint ReparseTag { get; init; }
    public bool IsDeleted { get; init; }
    public bool IsOrphan { get; init; }
    /// <summary>Full path from the volume root, backslash separated, set by the enumerator when known.</summary>
    public string Path { get; set; } = "";

    public bool IsReparsePoint => (Attributes & NtfsFileAttributes.ReparsePoint) != 0;
    public bool IsEncrypted => (Attributes & NtfsFileAttributes.Encrypted) != 0;
    public bool IsCompressed => (Attributes & NtfsFileAttributes.Compressed) != 0;
    public bool IsSparse => (Attributes & NtfsFileAttributes.SparseFile) != 0;
    public bool IsHidden => (Attributes & NtfsFileAttributes.Hidden) != 0;
    public bool IsSystem => (Attributes & NtfsFileAttributes.System) != 0;
    public bool IsMetaFile => Record < 24 && Name.StartsWith('$');
    public bool IsWofCompressed => ReparseTag == 0x80000017;
    public bool IsSymlinkOrJunction => ReparseTag is 0xA000000C or 0xA0000003;
    public string ReparseKind => ReparseTag switch { 0xA0000003 => "Junction", 0xA000000C => "Symbolic link", 0x80000017 => "WOF compressed (CompactOS)", 0x9000001A => "OneDrive placeholder", 0x8000001B => "AppExecLink", 0 => "", _ => $"Reparse 0x{ReparseTag:X8}" };
    public string Extension => IsDirectory ? "" : System.IO.Path.GetExtension(Name);
    public override string ToString() => (IsDirectory ? "[DIR] " : "") + Name;
}

public sealed class NtfsVolumeInfo
{
    public string Label { get; init; } = "";
    public byte MajorVersion { get; init; }
    public byte MinorVersion { get; init; }
    public ushort Flags { get; init; }
    public bool IsDirty => (Flags & 0x0001) != 0;
    public bool ChkdskModified => (Flags & 0x8000) != 0;
    public string Version => $"{MajorVersion}.{MinorVersion}";
    public string FlagsText
    {
        get
        {
            var l = new List<string>();
            if ((Flags & 0x1) != 0) l.Add("DIRTY (unclean shutdown)");
            if ((Flags & 0x2) != 0) l.Add("resize LogFile");
            if ((Flags & 0x4) != 0) l.Add("upgrade on mount");
            if ((Flags & 0x8) != 0) l.Add("mounted on NT4");
            if ((Flags & 0x10) != 0) l.Add("delete USN underway");
            if ((Flags & 0x20) != 0) l.Add("repair object IDs");
            if ((Flags & 0x8000) != 0) l.Add("modified by chkdsk");
            return l.Count == 0 ? "clean" : string.Join(", ", l);
        }
    }
}
