using System.IO.Compression;
using Nova4Me2.Core.Analysis;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Forensics;

public sealed class StegoFinding
{
    public Severity Severity { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public long? Offset { get; init; }
    public long? Length { get; init; }
    public string? Extension { get; init; }
    public bool Extractable => Offset != null && Length > 0;
    public override string ToString() => $"[{Severity}] {Title}: {Detail}";
}

public sealed class StegoReport
{
    public string Name { get; init; } = "";
    public long Length { get; init; }
    public string DetectedType { get; init; } = "unknown";
    public long? LogicalEnd { get; init; }
    public double OverallEntropy { get; init; }
    public List<double> EntropyProfile { get; init; } = new();
    public List<StegoFinding> Findings { get; } = new();
    public List<CarvedFile> Embedded { get; } = new();
    public double? LsbChiSquareScore { get; init; }
    public string LsbVerdict { get; init; } = "";
    public int Score => Findings.Count == 0 ? 0 : Math.Min(100, Findings.Sum(f => f.Severity switch { Severity.Critical => 40, Severity.Error => 25, Severity.Warning => 12, _ => 3 }));
}

/// <summary>Looks for data hidden in or appended to a file: trailing payloads, nested files, entropy anomalies, container-specific oddities and LSB steganography.</summary>
public static class StegoAnalyzer
{
    public static StegoReport Analyze(ICarveSource src, CancellationToken ct = default)
    {
        long len = src.Length;
        var head = new byte[(int)Math.Min(64 * 1024, len)];
        if (head.Length > 0) src.Read(0, head);
        var sig = FileCarver.Identify(head);
        long? logicalEnd = null;
        if (sig != null)
        {
            var m = FileCarver.Measure(src, sig, 0, len);
            if (m != null && m.Method != CarveMethod.HeaderAndCap) logicalEnd = m.Length;
        }
        var profile = Entropy.Profile(src, 0, len);
        double overall = profile.Count > 0 ? profile.Average() : 0;
        var rep = new StegoReport { Name = src.Name, Length = len, DetectedType = sig?.Name ?? FileCarver.DescribeContent(head), LogicalEnd = logicalEnd, OverallEntropy = overall, EntropyProfile = profile };
        var f = rep.Findings;
        if (sig == null) f.Add(new StegoFinding { Severity = Severity.Info, Title = "Unrecognised format", Detail = "No known signature at offset 0; container-specific checks skipped." });
        else f.Add(new StegoFinding { Severity = Severity.Info, Title = $"Detected: {sig.Name}", Detail = logicalEnd != null ? $"Logical end of the format at byte {logicalEnd:N0} of {len:N0}." : "The format's logical end could not be determined precisely." });

        // 1. Appended payload after the format's logical end (the most common "hide a file inside an image" trick).
        if (logicalEnd is { } le && le < len)
        {
            long extra = len - le;
            var tail = new byte[(int)Math.Min(4096, extra)];
            src.Read(le, tail);
            var tailSig = FileCarver.Identify(tail);
            double te = Entropy.Shannon(tail);
            bool zeros = tail.All(b => b == 0);
            if (!zeros || extra > 4096)
                f.Add(new StegoFinding
                {
                    Severity = tailSig != null ? Severity.Critical : extra > 64 ? Severity.Error : Severity.Warning,
                    Title = tailSig != null ? $"Appended file: {tailSig.Name}" : zeros ? "Trailing zero padding" : te > 7.3 ? "Appended high-entropy data (encrypted/compressed payload?)" : "Appended data after the logical end",
                    Detail = $"{Format.Bytes(extra)} follow the end of the {sig!.Name} (offset {le:N0}). {FileCarver.DescribeContent(tail)}; entropy {te:0.00} bits/byte.",
                    Offset = le, Length = extra, Extension = tailSig?.Extension ?? (te > 7.3 ? "bin" : "dat")
                });
        }

        // 2. Nested / embedded files anywhere inside.
        try
        {
            var nested = FileCarver.Scan(src, FileCarver.WholeSource(src), new CarveOptions { Alignment = 1, IncludeNested = true, ChunkSize = 4 << 20 }, null, ct)
                .Where(c => c.Offset > 0 && !(sig != null && c.Signature == sig && c.Offset == 0)).ToList();
            // JPEG thumbnails inside EXIF are normal; keep them but rank low. Skip tiny/low-confidence hits inside a valid container.
            foreach (var c in nested)
            {
                bool thumb = sig?.Name.StartsWith("JPEG") == true && c.Signature.Name.StartsWith("JPEG") && logicalEnd is { } l2 && c.Offset < l2 && c.Length < 256 * 1024;
                bool capped = c.Method == CarveMethod.HeaderAndCap;
                if (capped && c.Signature.Header.Length <= 3) continue; // "MZ"/"BM"-style false positives without validation of length
                rep.Embedded.Add(c);
                f.Add(new StegoFinding
                {
                    Severity = thumb ? Severity.Info : capped ? Severity.Warning : Severity.Error,
                    Title = thumb ? "Embedded thumbnail (normal for EXIF)" : $"Embedded {c.Signature.Name}",
                    Detail = $"At offset {c.Offset:N0}, {Format.Bytes(c.Length)}, {c.Confidence} confidence{(logicalEnd is { } l3 && c.Offset >= l3 ? " (in the appended region)" : "")}.",
                    Offset = c.Offset, Length = c.Length, Extension = c.Signature.Extension
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { f.Add(new StegoFinding { Severity = Severity.Info, Title = "Embedded-file scan incomplete", Detail = ex.Message }); }

        // 3. Entropy anomalies.
        if (profile.Count >= 8)
        {
            int tailBlocks = Math.Max(1, profile.Count / 8);
            double tailAvg = profile.TakeLast(tailBlocks).Average(), headAvg = profile.Take(profile.Count - tailBlocks).Average();
            if (tailAvg > 7.6 && headAvg < 7.0 && logicalEnd == null)
                f.Add(new StegoFinding { Severity = Severity.Warning, Title = "High-entropy tail", Detail = $"The last {tailBlocks} sample blocks average {tailAvg:0.00} bits/byte versus {headAvg:0.00} before: typical of an encrypted or compressed payload appended to the file." });
            if (sig != null && sig.Category == "Documents" && overall > 7.8)
                f.Add(new StegoFinding { Severity = Severity.Info, Title = "Unusually high entropy for a document", Detail = $"{overall:0.00} bits/byte overall." });
        }

        // 4. Container-specific checks.
        if (sig?.Name.StartsWith("PNG") == true) PngChecks(src, rep);
        if (sig?.Name.StartsWith("JPEG") == true) JpegChecks(src, rep);
        if (sig?.Name.StartsWith("ZIP") == true) ZipChecks(src, rep);

        // 5. LSB steganography (BMP / PNG).
        double? lsb = null; string verdict = "";
        try
        {
            var pixels = ImageDecoder.TryDecode(src, sig);
            if (pixels != null)
            {
                var r = LsbAnalysis.ChiSquare(pixels);
                lsb = r.Score;
                verdict = r.Verdict;
                f.Add(new StegoFinding
                {
                    Severity = r.Score > 0.7 ? Severity.Error : r.Score > 0.4 ? Severity.Warning : Severity.Good,
                    Title = r.Score > 0.7 ? "LSB steganography likely" : r.Score > 0.4 ? "LSB statistics unusual" : "No LSB steganography indicators",
                    Detail = $"{r.Verdict} ({pixels.Width}×{pixels.Height}, {pixels.Channels} channels; {r.BandsFlagged} of {r.Bands} bands look embedded)."
                });
            }
        }
        catch (Exception ex) { f.Add(new StegoFinding { Severity = Severity.Info, Title = "LSB analysis skipped", Detail = ex.Message }); }
        return new StegoReport
        {
            Name = rep.Name, Length = rep.Length, DetectedType = rep.DetectedType, LogicalEnd = rep.LogicalEnd, OverallEntropy = rep.OverallEntropy, EntropyProfile = rep.EntropyProfile,
            LsbChiSquareScore = lsb, LsbVerdict = verdict
        }.With(rep);
    }

    private static StegoReport With(this StegoReport target, StegoReport from)
    {
        target.Findings.AddRange(from.Findings);
        target.Embedded.AddRange(from.Embedded);
        return target;
    }

    private static void PngChecks(ICarveSource src, StegoReport rep)
    {
        var f = rep.Findings;
        long pos = 8;
        var hdr = new byte[8];
        bool afterIend = false;
        int guard = 0;
        var known = new HashSet<string> { "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "cHRM", "gAMA", "iCCP", "sBIT", "sRGB", "tEXt", "zTXt", "iTXt", "bKGD", "hIST", "pHYs", "sPLT", "tIME", "eXIf", "acTL", "fcTL", "fdAT", "oFFs", "pCAL", "sCAL", "gIFg", "gIFx", "sTER", "dSIG", "cICP", "mDCv", "cLLi" };
        while (pos + 8 <= src.Length && guard++ < 100000)
        {
            src.Read(pos, hdr);
            uint len = (uint)(hdr[0] << 24 | hdr[1] << 16 | hdr[2] << 8 | hdr[3]);
            string type = System.Text.Encoding.ASCII.GetString(hdr, 4, 4);
            if (afterIend)
            {
                // Only a well-formed chunk counts here; arbitrary appended bytes are already reported as an appended payload.
                if (type.All(char.IsLetter) && pos + 12 + len <= src.Length)
                    f.Add(new StegoFinding { Severity = Severity.Error, Title = "PNG chunk after IEND", Detail = $"Chunk '{type}' ({len:N0} bytes) at offset {pos:N0}: decoders stop at IEND, so this is invisible when viewed.", Offset = pos + 8, Length = len, Extension = "bin" });
                break;
            }
            if (!known.Contains(type) && type.All(c => char.IsLetter(c))) f.Add(new StegoFinding { Severity = Severity.Warning, Title = $"Unknown PNG chunk '{type}'", Detail = $"{len:N0} bytes at offset {pos:N0}; private/ancillary chunks are a common hiding place.", Offset = pos + 8, Length = len, Extension = "bin" });
            if (type is "tEXt" or "zTXt" or "iTXt" && len > 0)
            {
                var d = new byte[(int)Math.Min(len, 200)];
                src.Read(pos + 8, d);
                int nul = Array.IndexOf(d, (byte)0);
                string key = nul > 0 ? System.Text.Encoding.ASCII.GetString(d, 0, nul) : "?";
                f.Add(new StegoFinding { Severity = len > 4096 ? Severity.Warning : Severity.Info, Title = $"PNG text chunk '{key}'", Detail = $"{type}, {len:N0} bytes at offset {pos:N0}.", Offset = pos + 8, Length = len, Extension = "txt" });
            }
            if (type == "IEND") afterIend = true;
            pos += 12 + len;
        }
    }

    private static void JpegChecks(ICarveSource src, StegoReport rep)
    {
        var f = rep.Findings;
        long pos = 2;
        var hdr = new byte[4];
        int soi = 1, guard = 0;
        while (pos + 4 <= src.Length && guard++ < 10000)
        {
            src.Read(pos, hdr);
            if (hdr[0] != 0xFF) break;
            byte m = hdr[1];
            if (m == 0xD8) { soi++; pos += 2; continue; }
            if (m == 0xDA) break;
            if (m == 0xD9) break;
            int len = (hdr[2] << 8) | hdr[3];
            if (len < 2) break;
            if (m == 0xFE)
            {
                var c = new byte[Math.Min(len - 2, 256)];
                if (c.Length > 0) src.Read(pos + 4, c);
                string desc = FileCarver.DescribeContent(c);
                f.Add(new StegoFinding { Severity = desc.StartsWith("text") ? Severity.Info : Severity.Warning, Title = "JPEG comment segment", Detail = $"{len - 2:N0} bytes at offset {pos + 4:N0}: {desc}.", Offset = pos + 4, Length = len - 2, Extension = desc.StartsWith("text") ? "txt" : "bin" });
            }
            else if (m >= 0xE0 && m <= 0xEF && len > 64 * 1024)
                f.Add(new StegoFinding { Severity = Severity.Warning, Title = $"Large APP{m - 0xE0} segment", Detail = $"{len:N0} bytes at offset {pos:N0}; metadata segments this large can carry hidden payloads.", Offset = pos + 4, Length = len - 2, Extension = "bin" });
            pos += 2 + len;
        }
        if (soi > 1) f.Add(new StegoFinding { Severity = Severity.Info, Title = "Multiple SOI markers", Detail = $"{soi} start-of-image markers before the scan data (embedded thumbnails or concatenated images)." });
    }

    private static void ZipChecks(ICarveSource src, StegoReport rep)
    {
        var head = new byte[Math.Min(30, (int)src.Length)];
        src.Read(0, head);
        if (head.Length >= 8 && (Bin.U16(head, 6) & 0x1) != 0) rep.Findings.Add(new StegoFinding { Severity = Severity.Info, Title = "Encrypted ZIP entries", Detail = "The first local file header has the encryption flag set." });
    }

    /// <summary>Extract a finding's byte range to a file.</summary>
    public static string ExtractRange(ICarveSource src, long offset, long length, string destDir, string fileName)
    {
        Directory.CreateDirectory(destDir);
        string path = ForensicProject.UniquePath(destDir, fileName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[1 << 20];
        long pos = offset, remaining = Math.Min(length, src.Length - offset);
        while (remaining > 0) { int n = (int)Math.Min(buf.Length, remaining); src.Read(pos, buf.AsSpan(0, n)); fs.Write(buf, 0, n); pos += n; remaining -= n; }
        return path;
    }
}

public sealed class DecodedImage
{
    public int Width, Height, Channels;
    public byte[] Pixels = Array.Empty<byte>(); // row-major, Channels bytes per pixel
}

/// <summary>Minimal decoders (uncompressed BMP 24/32-bit, non-interlaced 8-bit PNG) for LSB analysis. No external dependencies.</summary>
public static class ImageDecoder
{
    public static DecodedImage? TryDecode(ICarveSource src, FileSignature? sig)
    {
        if (sig == null) return null;
        if (sig.Name.StartsWith("BMP")) return Bmp(src);
        if (sig.Name.StartsWith("PNG")) return Png(src);
        return null;
    }

    private static DecodedImage? Bmp(ICarveSource src)
    {
        var h = new byte[54];
        if (src.Length < 54) return null;
        src.Read(0, h);
        uint dataOff = Bin.U32(h, 10);
        int w = Bin.I32(h, 18), hgt = Bin.I32(h, 22);
        int bpp = Bin.U16(h, 28);
        uint comp = Bin.U32(h, 30);
        if (comp != 0 || (bpp != 24 && bpp != 32) || w <= 0 || Math.Abs(hgt) <= 0 || (long)w * Math.Abs(hgt) > 50_000_000) return null;
        int ch = bpp / 8;
        int stride = (w * ch + 3) & ~3;
        bool bottomUp = hgt > 0;
        int H = Math.Abs(hgt);
        var img = new DecodedImage { Width = w, Height = H, Channels = ch, Pixels = new byte[w * H * ch] };
        var row = new byte[stride];
        for (int y = 0; y < H; y++)
        {
            long off = dataOff + (long)y * stride;
            if (off + stride > src.Length) break;
            src.Read(off, row);
            int dy = bottomUp ? H - 1 - y : y;
            Array.Copy(row, 0, img.Pixels, dy * w * ch, w * ch);
        }
        return img;
    }

    private static DecodedImage? Png(ICarveSource src)
    {
        long pos = 8;
        var hdr = new byte[8];
        int w = 0, h = 0, depth = 0, ctype = 0, interlace = 0;
        var idat = new MemoryStream();
        while (pos + 8 <= src.Length)
        {
            src.Read(pos, hdr);
            uint len = (uint)(hdr[0] << 24 | hdr[1] << 16 | hdr[2] << 8 | hdr[3]);
            string type = System.Text.Encoding.ASCII.GetString(hdr, 4, 4);
            if (type == "IHDR")
            {
                var d = new byte[13]; src.Read(pos + 8, d);
                w = d[0] << 24 | d[1] << 16 | d[2] << 8 | d[3]; h = d[4] << 24 | d[5] << 16 | d[6] << 8 | d[7]; depth = d[8]; ctype = d[9]; interlace = d[12];
            }
            else if (type == "IDAT") { var d = new byte[len]; src.Read(pos + 8, d); idat.Write(d); }
            else if (type == "IEND") break;
            pos += 12 + len;
        }
        if (w <= 0 || h <= 0 || depth != 8 || interlace != 0 || (long)w * h > 50_000_000) return null;
        int ch = ctype switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
        if (ch == 0) return null;
        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        var data = raw.GetBuffer();
        int stride = w * ch;
        if (raw.Length < (long)(stride + 1) * h) return null;
        var img = new DecodedImage { Width = w, Height = h, Channels = ch, Pixels = new byte[stride * h] };
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int rowStart = y * (stride + 1);
            byte filter = data[rowStart];
            Array.Copy(data, rowStart + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= ch ? cur[i - ch] : 0, b = prev[i], c = i >= ch ? prev[i - ch] : 0;
                cur[i] = filter switch
                {
                    1 => (byte)(cur[i] + a), 2 => (byte)(cur[i] + b), 3 => (byte)(cur[i] + ((a + b) >> 1)), 4 => (byte)(cur[i] + Paeth(a, b, c)), _ => cur[i]
                };
            }
            Array.Copy(cur, 0, img.Pixels, y * stride, stride);
            (prev, cur) = (cur, prev);
        }
        return img;
    }

    private static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
}

public static class LsbAnalysis
{
    public sealed record Result(double Score, string Verdict, int Bands, int BandsFlagged);

    /// <summary>
    /// Chi-square attack (Westfeld &amp; Pfitzmann): LSB embedding evens out the counts of each pair of values (2k, 2k+1).
    /// Evaluated in horizontal bands; the fraction of bands that look "evened out" is the score.
    /// </summary>
    public static Result ChiSquare(DecodedImage img, int bands = 16)
    {
        int rowsPerBand = Math.Max(1, img.Height / bands);
        int flagged = 0, total = 0;
        int stride = img.Width * img.Channels;
        for (int b = 0; b < bands; b++)
        {
            int y0 = b * rowsPerBand, y1 = b == bands - 1 ? img.Height : Math.Min(img.Height, y0 + rowsPerBand);
            if (y0 >= y1) break;
            var hist = new long[256];
            for (int y = y0; y < y1; y++)
                for (int i = 0; i < stride; i++)
                {
                    if (img.Channels == 4 && i % 4 == 3) continue; // skip alpha
                    hist[img.Pixels[y * stride + i]]++;
                }
            double chi = 0; int df = 0;
            for (int k = 0; k < 128; k++)
            {
                double exp = (hist[2 * k] + hist[2 * k + 1]) / 2.0;
                if (exp < 5) continue;
                chi += (hist[2 * k] - exp) * (hist[2 * k] - exp) / exp;
                df++;
            }
            if (df < 8) continue;
            double p = 1 - Entropy.ChiSquareCdf(chi, df - 1); // high p ⇒ pairs suspiciously equal ⇒ embedded
            total++;
            if (p > 0.95) flagged++;
        }
        double score = total == 0 ? 0 : (double)flagged / total;
        string verdict = total == 0 ? "not enough pixel data" : score > 0.7 ? "pair statistics are evened out across the image, as LSB embedding does" : score > 0.4 ? "part of the image shows evened-out pair statistics (partial embedding or unusual content)" : "value-pair statistics look natural";
        return new Result(score, verdict, total, flagged);
    }

    /// <summary>Extract the LSB plane (all channels except alpha, scanline order) packed MSB-first, as tools like LSB-Steganography would read it.</summary>
    public static byte[] ExtractLsbPlane(DecodedImage img)
    {
        int stride = img.Width * img.Channels;
        var bits = new List<byte>();
        int acc = 0, n = 0;
        for (int y = 0; y < img.Height; y++)
            for (int i = 0; i < stride; i++)
            {
                if (img.Channels == 4 && i % 4 == 3) continue;
                acc = acc << 1 | (img.Pixels[y * stride + i] & 1);
                if (++n == 8) { bits.Add((byte)acc); acc = 0; n = 0; }
            }
        return bits.ToArray();
    }
}
