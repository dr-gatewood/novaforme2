using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Forensics;

public delegate long? LengthParserFn(ReadOnlySpan<byte> head, Func<long, int, byte[]> reader);
public delegate bool ValidateFn(ReadOnlySpan<byte> head);

/// <summary>How the end of a carved file was decided.</summary>
public enum CarveMethod { HeaderAndLength, HeaderAndFooter, HeaderAndCap }

/// <summary>A file-type signature: header bytes (at an offset), optional footer, size cap, and an optional exact-length parser.</summary>
public sealed class FileSignature
{
    public string Name { get; init; } = "";
    public string Extension { get; init; } = "";
    public string Category { get; init; } = "";
    public byte[] Header { get; init; } = Array.Empty<byte>();
    /// <summary>Offset of the header inside the file (0 for almost everything; 4 for "ftyp", 0x8001 for ISO).</summary>
    public int HeaderOffset { get; init; }
    public byte[]? Footer { get; init; }
    /// <summary>Extra bytes after the footer that belong to the file (e.g. ZIP end-of-central-directory comment length).</summary>
    public long MaxLength { get; init; } = 64L * 1024 * 1024;
    /// <summary>Given the first bytes of the candidate (up to <see cref="ProbeBytes"/>), returns the exact length or null.</summary>
    public LengthParserFn? LengthParser { get; init; }
    public int ProbeBytes { get; init; } = 4096;
    /// <summary>Extra validation of the header region to cut false positives.</summary>
    public ValidateFn? Validate { get; init; }
    public override string ToString() => $"{Name} (.{Extension})";
}

public static class Signatures
{
    public static readonly List<FileSignature> All = Build();

    public static IEnumerable<string> Categories => All.Select(s => s.Category).Distinct();

    private static byte[] B(params int[] v) => v.Select(x => (byte)x).ToArray();
    private static byte[] A(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    private static List<FileSignature> Build()
    {
        var l = new List<FileSignature>
        {
            new() { Name = "JPEG image", Extension = "jpg", Category = "Images", Header = B(0xFF, 0xD8, 0xFF), Footer = B(0xFF, 0xD9), MaxLength = 48L << 20, LengthParser = JpegLength, ProbeBytes = 64 << 10, Validate = b => b.Length > 3 && (b[3] == 0xE0 || b[3] == 0xE1 || b[3] == 0xE2 || b[3] == 0xDB || b[3] == 0xEE || b[3] == 0xC0 || b[3] == 0xFE || b[3] == 0xE8 || b[3] == 0xEC || b[3] == 0xED) },
            new() { Name = "PNG image", Extension = "png", Category = "Images", Header = B(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), Footer = A("IEND"), MaxLength = 64L << 20, LengthParser = PngLength },
            new() { Name = "GIF image", Extension = "gif", Category = "Images", Header = A("GIF8"), Footer = B(0x00, 0x3B), MaxLength = 32L << 20, Validate = b => b.Length > 5 && (b[4] == '7' || b[4] == '9') && b[5] == 'a' },
            new() { Name = "BMP image", Extension = "bmp", Category = "Images", Header = A("BM"), MaxLength = 128L << 20, LengthParser = (b, _) => b.Length >= 6 ? Bin.U32(b, 2) : null, Validate = b => b.Length >= 18 && Bin.U32(b, 2) > 54 && Bin.U32(b, 10) is >= 26 and < 4096 && Bin.U32(b, 14) is 12 or 40 or 52 or 56 or 108 or 124 },
            new() { Name = "TIFF image (LE)", Extension = "tif", Category = "Images", Header = B(0x49, 0x49, 0x2A, 0x00), MaxLength = 256L << 20 },
            new() { Name = "TIFF image (BE)", Extension = "tif", Category = "Images", Header = B(0x4D, 0x4D, 0x00, 0x2A), MaxLength = 256L << 20 },
            new() { Name = "WebP image", Extension = "webp", Category = "Images", Header = A("RIFF"), MaxLength = 64L << 20, LengthParser = RiffLength, Validate = b => b.Length >= 12 && b.Slice(8, 4).SequenceEqual(A("WEBP")) },
            new() { Name = "Photoshop document", Extension = "psd", Category = "Images", Header = A("8BPS"), MaxLength = 1L << 30 },
            new() { Name = "ICO icon", Extension = "ico", Category = "Images", Header = B(0, 0, 1, 0), MaxLength = 4L << 20, Validate = IcoValid, LengthParser = IcoLength },
            new() { Name = "PDF document", Extension = "pdf", Category = "Documents", Header = A("%PDF-"), Footer = A("%%EOF"), MaxLength = 256L << 20 },
            new() { Name = "ZIP archive / Office (docx, xlsx, pptx, jar, apk)", Extension = "zip", Category = "Archives", Header = B(0x50, 0x4B, 0x03, 0x04), MaxLength = 1L << 30, LengthParser = ZipLength, ProbeBytes = 1 << 20 },
            new() { Name = "RAR archive", Extension = "rar", Category = "Archives", Header = A("Rar!\x1A\x07"), MaxLength = 1L << 30 },
            new() { Name = "7-Zip archive", Extension = "7z", Category = "Archives", Header = B(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C), MaxLength = 1L << 30, LengthParser = SevenZipLength },
            new() { Name = "GZIP archive", Extension = "gz", Category = "Archives", Header = B(0x1F, 0x8B, 0x08), MaxLength = 1L << 30 },
            new() { Name = "BZIP2 archive", Extension = "bz2", Category = "Archives", Header = A("BZh"), MaxLength = 1L << 30, Validate = b => b.Length > 3 && b[3] >= '1' && b[3] <= '9' },
            new() { Name = "XZ archive", Extension = "xz", Category = "Archives", Header = B(0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00), MaxLength = 1L << 30 },
            new() { Name = "Microsoft cabinet", Extension = "cab", Category = "Archives", Header = A("MSCF"), MaxLength = 1L << 30, LengthParser = (b, _) => b.Length >= 12 ? Bin.U32(b, 8) : null },
            new() { Name = "OLE2 compound document (doc, xls, ppt, msi)", Extension = "ole", Category = "Documents", Header = B(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1), MaxLength = 256L << 20, LengthParser = OleLength },
            new() { Name = "RTF document", Extension = "rtf", Category = "Documents", Header = A("{\\rtf1"), Footer = A("}"), MaxLength = 32L << 20 },
            new() { Name = "SQLite database", Extension = "sqlite", Category = "Databases", Header = A("SQLite format 3\0"), MaxLength = 4L << 30, LengthParser = SqliteLength },
            new() { Name = "Windows executable / DLL", Extension = "exe", Category = "Executables", Header = A("MZ"), MaxLength = 256L << 20, LengthParser = PeLength, ProbeBytes = 8192, Validate = b => b.Length >= 0x40 && Bin.U32(b, 0x3C) is >= 0x40 and < 0x1000 },
            new() { Name = "ELF executable", Extension = "elf", Category = "Executables", Header = B(0x7F, 0x45, 0x4C, 0x46), MaxLength = 256L << 20 },
            new() { Name = "MP4 / MOV / M4A video", Extension = "mp4", Category = "Media", Header = A("ftyp"), HeaderOffset = 4, MaxLength = 8L << 30, LengthParser = Mp4Length },
            new() { Name = "AVI / WAV (RIFF)", Extension = "riff", Category = "Media", Header = A("RIFF"), MaxLength = 4L << 30, LengthParser = RiffLength, Validate = b => b.Length >= 12 && (b.Slice(8, 4).SequenceEqual(A("AVI ")) || b.Slice(8, 4).SequenceEqual(A("WAVE"))) },
            new() { Name = "MP3 audio (ID3)", Extension = "mp3", Category = "Media", Header = A("ID3"), MaxLength = 256L << 20 },
            new() { Name = "FLAC audio", Extension = "flac", Category = "Media", Header = A("fLaC"), MaxLength = 1L << 30 },
            new() { Name = "OGG audio/video", Extension = "ogg", Category = "Media", Header = A("OggS"), MaxLength = 1L << 30 },
            new() { Name = "Matroska / WebM", Extension = "mkv", Category = "Media", Header = B(0x1A, 0x45, 0xDF, 0xA3), MaxLength = 8L << 30 },
            new() { Name = "Windows shortcut", Extension = "lnk", Category = "Windows", Header = B(0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00), MaxLength = 64 << 10 },
            new() { Name = "Registry hive", Extension = "hive", Category = "Windows", Header = A("regf"), MaxLength = 512L << 20 },
            new() { Name = "Windows event log", Extension = "evtx", Category = "Windows", Header = A("ElfFile\0"), MaxLength = 1L << 30 },
            new() { Name = "Outlook PST/OST", Extension = "pst", Category = "Email", Header = A("!BDN"), MaxLength = 16L << 30 },
            new() { Name = "Email message", Extension = "eml", Category = "Email", Header = A("Received: "), MaxLength = 32L << 20 },
            new() { Name = "vCard", Extension = "vcf", Category = "Documents", Header = A("BEGIN:VCARD"), Footer = A("END:VCARD"), MaxLength = 1 << 20 },
            new() { Name = "iCalendar", Extension = "ics", Category = "Documents", Header = A("BEGIN:VCALENDAR"), Footer = A("END:VCALENDAR"), MaxLength = 8 << 20 },
            new() { Name = "XML document", Extension = "xml", Category = "Documents", Header = A("<?xml version="), MaxLength = 32L << 20 },
            new() { Name = "HTML document", Extension = "html", Category = "Documents", Header = A("<!DOCTYPE html"), Footer = A("</html>"), MaxLength = 32L << 20 },
            new() { Name = "PEM key / certificate", Extension = "pem", Category = "Keys", Header = A("-----BEGIN "), Footer = A("-----END "), MaxLength = 64 << 10 },
            new() { Name = "ISO 9660 image", Extension = "iso", Category = "Disk images", Header = A("CD001"), HeaderOffset = 0x8001, MaxLength = 16L << 30 },
            new() { Name = "VHD image footer", Extension = "vhd", Category = "Disk images", Header = A("conectix"), MaxLength = 512 },
            new() { Name = "Java class", Extension = "class", Category = "Executables", Header = B(0xCA, 0xFE, 0xBA, 0xBE), MaxLength = 16 << 20 },
            new() { Name = "Windows memory dump", Extension = "dmp", Category = "Windows", Header = A("PAGEDU"), MaxLength = 64L << 30 },
            new() { Name = "Bitcoin wallet (Berkeley DB)", Extension = "dat", Category = "Keys", Header = B(0x00, 0x00, 0x00, 0x00, 0x62, 0x31, 0x05, 0x00), HeaderOffset = 8, MaxLength = 256L << 20 },
        };
        return l;
    }

    private static bool IcoValid(ReadOnlySpan<byte> b)
    {
        if (b.Length < 22) return false;
        int count = Bin.U16(b, 4);
        if (count is <= 0 or >= 64) return false;
        // first directory entry: reserved 0, planes 0/1, bpp in a sane set, size > 0, offset after the directory
        int bpp = Bin.U16(b, 6 + 12);
        return b[6 + 3] == 0 && Bin.U16(b, 6 + 4) <= 1 && bpp is 0 or 1 or 4 or 8 or 16 or 24 or 32 && Bin.U32(b, 6 + 8) > 0 && Bin.U32(b, 6 + 12) >= (uint)(6 + 16 * count);
    }

    private static long? IcoLength(ReadOnlySpan<byte> b, Func<long, int, byte[]> _)
    {
        int count = Bin.U16(b, 4);
        long end = 0;
        for (int i = 0; i < count && 6 + 16 * (i + 1) <= b.Length; i++) end = Math.Max(end, (long)Bin.U32(b, 6 + 16 * i + 12) + Bin.U32(b, 6 + 16 * i + 8));
        return end > 0 ? end : null;
    }

    // ---- exact-length parsers (reader(offset,count) fetches more bytes of the candidate when needed) ----

    private static long? JpegLength(ReadOnlySpan<byte> b, Func<long, int, byte[]> reader)
    {
        // Walk the marker segments until SOS; after SOS the entropy-coded data ends at the EOI (FFD9) that is not followed by another marker.
        int i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF) return null;
            byte m = b[i + 1];
            if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01) { i += 2; continue; }
            if (m == 0xDA) break; // SOS: scan data follows
            int len = (b[i + 2] << 8) | b[i + 3];
            if (len < 2) return null;
            i += 2 + len;
        }
        return null; // fall back to the footer search (handled by the carver, which also validates thumbnails)
    }

    private static long? PngLength(ReadOnlySpan<byte> b, Func<long, int, byte[]> reader)
    {
        long pos = 8;
        int guard = 0;
        byte[] cur = b.ToArray();
        while (guard++ < 100000)
        {
            if (pos + 8 > cur.Length)
            {
                if (pos > 512L << 20) return null;
                cur = reader(0, (int)Math.Min(int.MaxValue, Math.Max(cur.Length * 2L, pos + 8 + 65536)));
                if (pos + 8 > cur.Length) return null;
            }
            uint len = (uint)(cur[pos] << 24 | cur[pos + 1] << 16 | cur[pos + 2] << 8 | cur[pos + 3]);
            string type = System.Text.Encoding.ASCII.GetString(cur, (int)pos + 4, 4);
            if (len > 256 << 20) return null;
            pos += 12 + len;
            if (type == "IEND") return pos;
        }
        return null;
    }

    private static long? RiffLength(ReadOnlySpan<byte> b, Func<long, int, byte[]> _) => b.Length >= 8 ? 8 + (long)Bin.U32(b, 4) : null;

    private static long? Mp4Length(ReadOnlySpan<byte> b, Func<long, int, byte[]> reader)
    {
        long pos = 0;
        byte[] cur = b.ToArray();
        int guard = 0;
        while (guard++ < 10000)
        {
            if (pos + 16 > cur.Length) { cur = reader(0, (int)Math.Min(int.MaxValue, pos + 16)); if (pos + 16 > cur.Length) return pos > 0 ? pos : null; }
            long size = (uint)(cur[pos] << 24 | cur[pos + 1] << 16 | cur[pos + 2] << 8 | cur[pos + 3]);
            string type = System.Text.Encoding.ASCII.GetString(cur, (int)pos + 4, 4);
            if (size == 1) { size = 0; for (int i = 0; i < 8; i++) size = size << 8 | cur[pos + 8 + i]; }
            else if (size == 0) return null;
            if (size < 8) return pos > 0 ? pos : null;
            bool known = type is "ftyp" or "moov" or "mdat" or "free" or "skip" or "wide" or "uuid" or "meta" or "moof" or "mfra" or "pdin" or "sidx" or "styp" or "ssix" or "prft";
            if (!known) return pos > 0 ? pos : null;
            pos += size;
            if (pos > 16L << 30) return null;
            if (type == "mdat" || type == "moov")
            {
                // keep walking boxes until an unknown type appears; we need the box at 'pos' to decide
            }
        }
        return pos;
    }

    private static long? ZipLength(ReadOnlySpan<byte> b, Func<long, int, byte[]> reader)
    {
        // Search forward for the End Of Central Directory record (PK\5\6) and add its comment length.
        var eocd = new byte[] { 0x50, 0x4B, 0x05, 0x06 };
        long window = b.Length;
        byte[] cur = b.ToArray();
        int from = 0;
        while (true)
        {
            int idx = cur.AsSpan(from).IndexOf(eocd);
            if (idx >= 0)
            {
                int at = from + idx;
                if (at + 22 <= cur.Length)
                {
                    int comment = Bin.U16(cur, at + 20);
                    long end = at + 22 + comment;
                    // Sanity: the central directory offset must point inside this file.
                    uint cdOff = Bin.U32(cur, at + 16);
                    if (cdOff < end) return end;
                }
                from = at + 4;
                continue;
            }
            if (window >= 1L << 30) return null;
            window = Math.Min(1L << 30, window * 4);
            cur = reader(0, (int)window);
            from = Math.Max(0, cur.Length - (int)window + from);
            if (cur.Length < window) { int i2 = cur.AsSpan(from).IndexOf(eocd); if (i2 < 0) return null; }
        }
    }

    private static long? SevenZipLength(ReadOnlySpan<byte> b, Func<long, int, byte[]> _)
    {
        if (b.Length < 32) return null;
        long nextOff = Bin.I64(b, 12), nextSize = Bin.I64(b, 20);
        if (nextOff < 0 || nextSize < 0 || nextOff > 16L << 30) return null;
        return 32 + nextOff + nextSize;
    }

    private static long? OleLength(ReadOnlySpan<byte> b, Func<long, int, byte[]> reader)
    {
        if (b.Length < 512) return null;
        int sectorShift = Bin.U16(b, 0x1E);
        if (sectorShift is not (9 or 12)) return null;
        long sector = 1L << sectorShift;
        uint fatSectors = Bin.U32(b, 0x2C);
        // Total sectors = number of FAT entries ≈ fatSectors * (sector/4); the used count is what matters, but FAT sizing gives a safe upper bound.
        long total = (long)fatSectors * (sector / 4);
        if (total <= 0 || total > (2L << 30) / sector) return null;
        return Math.Min((1 + total) * sector, 2L << 30);
    }

    private static long? SqliteLength(ReadOnlySpan<byte> b, Func<long, int, byte[]> _)
    {
        if (b.Length < 100) return null;
        int pageSize = (b[16] << 8) | b[17];
        if (pageSize == 1) pageSize = 65536;
        uint pages = (uint)(b[28] << 24 | b[29] << 16 | b[30] << 8 | b[31]);
        if (pageSize < 512 || pages == 0) return null;
        return (long)pageSize * pages;
    }

    private static long? PeLength(ReadOnlySpan<byte> b, Func<long, int, byte[]> reader)
    {
        if (b.Length < 0x40) return null;
        int pe = (int)Bin.U32(b, 0x3C);
        if (pe + 24 > b.Length) return null;
        if (!(b[pe] == 'P' && b[pe + 1] == 'E' && b[pe + 2] == 0 && b[pe + 3] == 0)) return null;
        int sections = Bin.U16(b, pe + 6);
        int optSize = Bin.U16(b, pe + 20);
        int secTable = pe + 24 + optSize;
        if (sections <= 0 || sections > 96 || secTable + sections * 40 > b.Length) return null;
        long end = 0;
        for (int i = 0; i < sections; i++)
        {
            int o = secTable + i * 40;
            long raw = Bin.U32(b, o + 20), size = Bin.U32(b, o + 16);
            end = Math.Max(end, raw + size);
        }
        // Certificate table (authenticode) may follow the sections.
        if (optSize >= 144)
        {
            int magic = Bin.U16(b, pe + 24);
            int certDir = magic == 0x20B ? pe + 24 + 144 : pe + 24 + 128;
            if (certDir + 8 <= b.Length) { long cOff = Bin.U32(b, certDir), cSize = Bin.U32(b, certDir + 4); if (cOff > 0) end = Math.Max(end, cOff + cSize); }
        }
        return end > 0 ? end : null;
    }
}
