using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Ntfs;

public static class AttrType
{
    public const uint StandardInformation = 0x10;
    public const uint AttributeList = 0x20;
    public const uint FileName = 0x30;
    public const uint ObjectId = 0x40;
    public const uint SecurityDescriptor = 0x50;
    public const uint VolumeName = 0x60;
    public const uint VolumeInformation = 0x70;
    public const uint Data = 0x80;
    public const uint IndexRoot = 0x90;
    public const uint IndexAllocation = 0xA0;
    public const uint Bitmap = 0xB0;
    public const uint ReparsePoint = 0xC0;
    public const uint EaInformation = 0xD0;
    public const uint Ea = 0xE0;
    public const uint LoggedUtilityStream = 0x100;
    public const uint End = 0xFFFFFFFF;

    public static string Name(uint t) => t switch
    {
        StandardInformation => "$STANDARD_INFORMATION", AttributeList => "$ATTRIBUTE_LIST", FileName => "$FILE_NAME", ObjectId => "$OBJECT_ID",
        SecurityDescriptor => "$SECURITY_DESCRIPTOR", VolumeName => "$VOLUME_NAME", VolumeInformation => "$VOLUME_INFORMATION", Data => "$DATA",
        IndexRoot => "$INDEX_ROOT", IndexAllocation => "$INDEX_ALLOCATION", Bitmap => "$BITMAP", ReparsePoint => "$REPARSE_POINT",
        EaInformation => "$EA_INFORMATION", Ea => "$EA", LoggedUtilityStream => "$LOGGED_UTILITY_STREAM", _ => $"0x{t:X}"
    };
}

[Flags]
public enum NtfsFileAttributes : uint
{
    ReadOnly = 0x1, Hidden = 0x2, System = 0x4, Directory = 0x10, Archive = 0x20, Device = 0x40, Normal = 0x80, Temporary = 0x100, SparseFile = 0x200,
    ReparsePoint = 0x400, Compressed = 0x800, Offline = 0x1000, NotContentIndexed = 0x2000, Encrypted = 0x4000, IntegrityStream = 0x8000,
    DirectoryIndex = 0x10000000, IndexView = 0x20000000
}

public sealed class DataRun
{
    public long Vcn;
    public long Lcn; // -1 = sparse / unallocated
    public long Count;
    public bool IsSparse => Lcn < 0;
    public override string ToString() => IsSparse ? $"VCN {Vcn} +{Count} (sparse)" : $"VCN {Vcn} -> LCN {Lcn} +{Count}";
}

/// <summary>One attribute as stored in an MFT record (a fragment of a logical attribute when attribute lists are involved).</summary>
public sealed class NtfsAttribute
{
    public uint Type;
    public string Name = "";
    public ushort Id;
    public ushort Flags;
    public bool NonResident;
    public byte[] ResidentValue = Array.Empty<byte>();
    public long StartVcn;
    public long LastVcn;
    public long AllocatedSize;
    public long RealSize;
    public long InitializedSize;
    public int CompressionUnit;
    public List<DataRun> Runs = new();
    public long OwnerRecord;
    public bool RunListTruncated;

    public bool IsCompressed => (Flags & 0x00FF) != 0; // low byte = compression format (1 = LZNT1)
    public bool IsEncrypted => (Flags & 0x4000) != 0;
    public bool IsSparse => (Flags & 0x8000) != 0;
    public long Length => NonResident ? RealSize : ResidentValue.Length;
    public string TypeName => AttrType.Name(Type);
    public override string ToString() => $"{TypeName}{(Name.Length > 0 ? ":" + Name : "")} {(NonResident ? $"non-resident {RealSize} bytes, {Runs.Count} runs" : $"resident {ResidentValue.Length} bytes")}";
}

public sealed class NtfsFileName
{
    public long ParentRecord;
    public ushort ParentSequence;
    public DateTime Created, Modified, MftModified, Accessed;
    public long AllocatedSize, RealSize;
    public NtfsFileAttributes Flags;
    public uint ReparseTag;
    public string Name = "";
    public byte Namespace; // 0 POSIX, 1 Win32, 2 DOS, 3 Win32+DOS
    public bool IsDosOnly => Namespace == 2;

    public static NtfsFileName? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 0x42) return null;
        int nameLen = b[0x40];
        if (0x42 + nameLen * 2 > b.Length) return null;
        ulong pref = Bin.U64(b, 0);
        return new NtfsFileName
        {
            ParentRecord = (long)(pref & 0xFFFFFFFFFFFFUL), ParentSequence = (ushort)(pref >> 48),
            Created = Format.FromFileTime(Bin.U64(b, 8)), Modified = Format.FromFileTime(Bin.U64(b, 16)), MftModified = Format.FromFileTime(Bin.U64(b, 24)), Accessed = Format.FromFileTime(Bin.U64(b, 32)),
            AllocatedSize = Bin.I64(b, 40), RealSize = Bin.I64(b, 48), Flags = (NtfsFileAttributes)Bin.U32(b, 56), ReparseTag = Bin.U32(b, 60),
            Name = Bin.Utf16(b, 0x42, nameLen), Namespace = b[0x41]
        };
    }
}

public sealed class NtfsStandardInfo
{
    public DateTime Created, Modified, MftModified, Accessed;
    public NtfsFileAttributes Flags;
    public uint OwnerId, SecurityId;
    public ulong Usn;

    public static NtfsStandardInfo? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 0x24) return null;
        var s = new NtfsStandardInfo
        {
            Created = Format.FromFileTime(Bin.U64(b, 0)), Modified = Format.FromFileTime(Bin.U64(b, 8)), MftModified = Format.FromFileTime(Bin.U64(b, 16)), Accessed = Format.FromFileTime(Bin.U64(b, 24)),
            Flags = (NtfsFileAttributes)Bin.U32(b, 32)
        };
        if (b.Length >= 0x48) { s.OwnerId = Bin.U32(b, 0x30); s.SecurityId = Bin.U32(b, 0x34); s.Usn = Bin.U64(b, 0x40); }
        return s;
    }
}

public sealed class AttributeListEntry
{
    public uint Type;
    public string Name = "";
    public long StartVcn;
    public long RecordNumber;
    public ushort RecordSequence;
    public ushort AttributeId;

    public static List<AttributeListEntry> Parse(ReadOnlySpan<byte> b)
    {
        var list = new List<AttributeListEntry>();
        int o = 0;
        while (o + 0x1A <= b.Length)
        {
            uint type = Bin.U32(b, o);
            int len = Bin.U16(b, o + 4);
            if (type == 0 || len < 0x1A || o + len > b.Length) break;
            int nameLen = b[o + 6], nameOff = b[o + 7];
            ulong rf = Bin.U64(b, o + 0x10);
            list.Add(new AttributeListEntry
            {
                Type = type, StartVcn = Bin.I64(b, o + 8), RecordNumber = (long)(rf & 0xFFFFFFFFFFFFUL), RecordSequence = (ushort)(rf >> 48), AttributeId = Bin.U16(b, o + 0x18),
                Name = nameLen > 0 && nameOff + nameLen * 2 <= len ? Bin.Utf16(b, o + nameOff, nameLen) : ""
            });
            o += len;
        }
        return list;
    }
}

public class NtfsException : IOException
{
    public NtfsException(string message, Exception? inner = null) : base(message, inner) { }
}
