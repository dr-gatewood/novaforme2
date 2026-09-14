using System.Security.Cryptography;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Recovery;

/// <summary>
/// Fixed-size VHD = raw disk image + 512-byte footer. Appending the footer turns a Nova4Me2 .img into a file that
/// Windows Disk Management can attach (Action → Attach VHD), which mounts the NTFS volume with a normal drive letter.
/// </summary>
public static class VhdFooter
{
    public const int Size = 512;

    public static byte[] Build(long rawLength)
    {
        var f = new byte[Size];
        "conectix"u8.CopyTo(f);                       // cookie
        PutBE32(f, 8, 0x00000002);                    // features: reserved bit must be set
        PutBE32(f, 12, 0x00010000);                   // format version 1.0
        for (int i = 16; i < 24; i++) f[i] = 0xFF;    // data offset: none (fixed disk)
        PutBE32(f, 24, (uint)Math.Max(0, (DateTime.UtcNow - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds));
        "nova"u8.CopyTo(f.AsSpan(28));                // creator application
        PutBE32(f, 32, 0x00010000);                   // creator version
        "Wi2k"u8.CopyTo(f.AsSpan(36));                // creator host OS
        PutBE64(f, 40, (ulong)rawLength);             // original size
        PutBE64(f, 48, (ulong)rawLength);             // current size
        // Geometry per the VHD spec algorithm.
        long totalSectors = rawLength / 512;
        uint cyl; byte heads, spt;
        if (totalSectors > 65535L * 16 * 255) totalSectors = 65535L * 16 * 255;
        if (totalSectors >= 65535L * 16 * 63) { spt = 255; heads = 16; cyl = (uint)(totalSectors / spt / heads); }
        else
        {
            spt = 17;
            long cylHeads = totalSectors / spt;
            heads = (byte)Math.Max(4, (cylHeads + 1023) / 1024);
            if (cylHeads >= heads * 1024L || heads > 16) { spt = 31; heads = 16; cylHeads = totalSectors / spt; }
            if (cylHeads >= heads * 1024L) { spt = 63; heads = 16; cylHeads = totalSectors / spt; }
            cyl = (uint)(cylHeads / heads);
        }
        f[56] = (byte)(cyl >> 8); f[57] = (byte)cyl; f[58] = heads; f[59] = spt;
        PutBE32(f, 60, 2);                            // disk type: fixed
        Guid.NewGuid().ToByteArray().CopyTo(f, 68);   // unique id
        f[84] = 0;                                    // saved state
        uint sum = 0;
        foreach (var b in f) sum += b;
        PutBE32(f, 64, ~sum);                         // checksum: one's complement of the byte sum (checksum field zero while summing)
        return f;
    }

    /// <summary>True if the file already ends with a VHD footer.</summary>
    public static bool HasFooter(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < Size) return false;
        fs.Seek(-Size, SeekOrigin.End);
        var b = new byte[8];
        return fs.Read(b, 0, 8) == 8 && b.AsSpan().SequenceEqual("conectix"u8);
    }

    /// <summary>Append a footer to a raw image (the raw part must be a multiple of 512 bytes; it is padded if not). Optionally rename to .vhd.</summary>
    public static string Append(string path, bool renameToVhd = true)
    {
        if (HasFooter(path)) return path;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            long len = fs.Length;
            long padded = Bin.AlignUp(len, 512);
            if (padded != len) fs.SetLength(padded);
            fs.Seek(0, SeekOrigin.End);
            fs.Write(Build(padded));
            fs.Flush(true);
        }
        if (!renameToVhd || path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase)) return path;
        string target = Path.ChangeExtension(path, ".vhd");
        if (File.Exists(target)) return path;
        File.Move(path, target);
        Log.Info($"Converted {Path.GetFileName(path)} to fixed VHD {Path.GetFileName(target)}");
        return target;
    }

    /// <summary>Remove the footer again (turns the file back into a plain raw image).</summary>
    public static void Strip(string path)
    {
        if (!HasFooter(path)) return;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.SetLength(fs.Length - Size);
    }

    private static void PutBE32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    private static void PutBE64(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (56 - 8 * i)); }
}
