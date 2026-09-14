using System.Security.Cryptography;
using Nova4Me2.Core.Analysis;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Forensics;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Recovery;
using Xunit;
using Xunit.Abstractions;

namespace Nova4Me2.Tests;

public class ForensicsTests(ITestOutputHelper output)
{
    private static bool Available => TestImages.Dir != null;

    private static (ResilientBlockDevice dev, NtfsVolume vol) Open()
    {
        var dev = ResilientBlockDevice.ForImage(TestImages.Plain);
        var vol = NtfsVolume.Open(dev, VolumeLocator.Find(dev)[0]);
        return (dev, vol);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void Carve_UnallocatedSpace_RecoversDeletedPngExactly()
    {
        if (!Available) return;
        var (dev, vol) = Open();
        using (dev)
        {
            var bitmap = ClusterBitmap.Load(vol);
            Assert.True(bitmap.FreeClusters > 0);
            var regions = bitmap.UnallocatedRanges().Select(r => (vol.Offset + r.Lcn * (long)vol.ClusterSize, r.Count * (long)vol.ClusterSize)).ToList();
            using var src = new DeviceCarveSource(dev);
            var found = FileCarver.Scan(src, regions, new CarveOptions { Alignment = vol.ClusterSize, Types = new HashSet<string> { "PNG image" } });
            var expected = File.ReadAllBytes(Path.Combine(TestImages.Dir!, "deleted-photo.expected"));
            var hit = found.FirstOrDefault(f => f.Length == expected.Length);
            Assert.True(hit != null, $"deleted PNG not carved; found {found.Count}: {string.Join(", ", found.Select(f => f.ToString()))}");
            Assert.Equal(CarveMethod.HeaderAndLength, hit!.Method);
            string dir = Path.Combine(Path.GetTempPath(), "nova-carve-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = FileCarver.Extract(src, hit, dir);
                Assert.Equal(Sha(expected), Sha(File.ReadAllBytes(path)));
                Assert.Equal(hit.Sha256, Sha(expected));
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public void Stego_FindsAppendedPdfInJpeg_AndZipAfterPngIend()
    {
        if (!Available) return;
        var (dev, vol) = Open();
        using (dev)
        {
            var jpg = vol.Resolve(@"forensics\photo.jpg")!;
            using var src = new StreamCarveSource(vol.OpenFile(jpg), "photo.jpg");
            var rep = StegoAnalyzer.Analyze(src);
            output.WriteLine(string.Join("\n", rep.Findings));
            Assert.StartsWith("JPEG", rep.DetectedType);
            Assert.NotNull(rep.LogicalEnd);
            Assert.Equal(632, rep.LogicalEnd);
            var appended = rep.Findings.FirstOrDefault(f => f.Title.Contains("Appended file: PDF"));
            Assert.NotNull(appended);
            Assert.Equal(632, appended!.Offset);
            Assert.True(rep.Score >= 40);

            var clean = vol.Resolve(@"forensics\photo-clean.jpg")!;
            using var src2 = new StreamCarveSource(vol.OpenFile(clean), "photo-clean.jpg");
            var rep2 = StegoAnalyzer.Analyze(src2);
            Assert.DoesNotContain(rep2.Findings, f => f.Severity >= Severity.Error);

            var png = vol.Resolve(@"forensics\image.png")!;
            using var src3 = new StreamCarveSource(vol.OpenFile(png), "image.png");
            var rep3 = StegoAnalyzer.Analyze(src3);
            output.WriteLine(string.Join("\n", rep3.Findings));
            Assert.Contains(rep3.Findings, f => f.Title.Contains("Appended file: ZIP"));
            Assert.Contains(rep3.Findings, f => f.Title.Contains("text chunk 'Comment'"));
            Assert.Contains(rep3.Embedded, e => e.Signature.Name.StartsWith("ZIP") && e.Method == CarveMethod.HeaderAndLength);
        }
    }

    [Fact]
    public void Stego_LsbChiSquare_FlagsEmbeddedBmpNotCleanOne()
    {
        if (!Available) return;
        var (dev, vol) = Open();
        using (dev)
        {
            using var s1 = new StreamCarveSource(vol.OpenFile(vol.Resolve(@"forensics\stego.bmp")!), "stego.bmp");
            var r1 = StegoAnalyzer.Analyze(s1);
            using var s2 = new StreamCarveSource(vol.OpenFile(vol.Resolve(@"forensics\clean.bmp")!), "clean.bmp");
            var r2 = StegoAnalyzer.Analyze(s2);
            output.WriteLine($"stego score {r1.LsbChiSquareScore} ({r1.LsbVerdict}); clean score {r2.LsbChiSquareScore} ({r2.LsbVerdict})");
            Assert.NotNull(r1.LsbChiSquareScore); Assert.NotNull(r2.LsbChiSquareScore);
            Assert.True(r1.LsbChiSquareScore > 0.7, "embedded BMP should score high");
            Assert.True(r2.LsbChiSquareScore < 0.4, "clean BMP should score low");
            // PNG decoder path: the gradient PNG decodes and yields an LSB verdict too.
            using var s3 = new StreamCarveSource(vol.OpenFile(vol.Resolve(@"forensics\image.png")!), "image.png");
            Assert.NotNull(StegoAnalyzer.Analyze(s3).LsbChiSquareScore);
        }
    }

    [Fact]
    public void Ads_ScanFindsHiddenPngAndZoneIdentifier_AndExtracts()
    {
        if (!Available) return;
        var (dev, vol) = Open();
        using (dev)
        {
            var list = AdsScanner.Scan(new IndexDirectorySource(vol));
            output.WriteLine(string.Join("\n", list.Select(a => $"{a.Display} {a.Size} {a.Kind} [{a.Content}]")));
            var png = list.FirstOrDefault(a => a.StreamName == "hidden.png");
            Assert.NotNull(png);
            Assert.Contains("PNG", png!.Content);
            Assert.Equal("png", png.KnownExtension);
            var zone = list.FirstOrDefault(a => a.StreamName == "Zone.Identifier");
            Assert.NotNull(zone);
            Assert.Contains("Mark of the Web", zone!.Kind);
            Assert.Contains(list, a => a.StreamName == "secret" && a.File.Name == "hello.txt");
            string dir = Path.Combine(Path.GetTempPath(), "nova-ads-" + Guid.NewGuid().ToString("N"));
            try
            {
                var p = AdsScanner.Extract(vol, png, dir);
                byte[] expected;
                using (var s = vol.OpenFile(vol.Resolve(@"forensics\hidden.expected")!)) expected = NtfsVolume.ReadAll(s);
                Assert.Equal(Sha(expected), Sha(File.ReadAllBytes(p)));
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public void SleuthKit_FsStat_IStat_Fls_Timeline_Blkls_BlkStat_Project()
    {
        if (!Available) return;
        var (dev, vol) = Open();
        using (dev)
        {
            var bitmap = ClusterBitmap.Load(vol);
            string fsstat = NtfsForensics.FsStat(vol, bitmap);
            Assert.Contains("Cluster Size: 4096", fsstat);
            Assert.Contains("Allocated clusters", fsstat);
            string istat = NtfsForensics.IStat(vol, 5);
            Assert.Contains("$INDEX_ROOT", istat);
            Assert.Contains("Allocated Directory", istat);
            var hello = vol.Resolve(@"Users\Alice\Documents\hello.txt")!;
            string istatFile = NtfsForensics.IStat(vol, hello.Record);
            Assert.Contains("hello.txt", istatFile);
            Assert.Contains("$DATA", istatFile);

            var src = new IndexDirectorySource(vol);
            var rows = NtfsForensics.Fls(src, includeDeleted: false);
            Assert.True(rows.Count > 700, $"fls rows {rows.Count}");
            var events = NtfsForensics.Timeline(rows).ToList();
            Assert.True(events.Count >= rows.Count);
            for (int i = 1; i < events.Count; i++) Assert.True(events[i].Time >= events[i - 1].Time);
            Assert.Contains(events, e => e.Macb.Contains('b'));

            string tmp = Path.Combine(Path.GetTempPath(), "nova-tsk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                NtfsForensics.WriteBodyFile(rows, Path.Combine(tmp, "body.txt"));
                NtfsForensics.WriteCsv(rows, Path.Combine(tmp, "fls.csv"));
                Assert.True(File.ReadAllLines(Path.Combine(tmp, "body.txt")).Length == rows.Count);
                Assert.Equal(rows.Count + 1, File.ReadAllLines(Path.Combine(tmp, "fls.csv")).Length);

                long written = NtfsForensics.Blkls(vol, bitmap, Path.Combine(tmp, "unalloc.bin"));
                Assert.Equal(bitmap.FreeClusters * vol.ClusterSize, written);
                Assert.Equal(written, new FileInfo(Path.Combine(tmp, "unalloc.bin")).Length);

                var owners = ClusterOwnerMap.Build(vol);
                var big = vol.Resolve(@"big\random-20MiB.bin")!;
                var data = vol.FindAttribute(vol.GetRecord(big.Record), AttrType.Data)!;
                long lcn = data.Runs[0].Lcn + 1;
                var owner = owners.Find(lcn);
                Assert.NotNull(owner);
                Assert.Equal(big.Record, owner!.Record);
                string stat = NtfsForensics.BlkStat(vol, bitmap, owners, lcn);
                Assert.Contains("Allocated", stat);
                Assert.Contains($"MFT entry {big.Record}", stat);
                var hex = NtfsForensics.HexDump(NtfsForensics.BlkCat(vol, lcn), 0, 64);
                Assert.Contains("00000000", hex);

                var proj = ForensicProject.Create("Unit Test", "plain.img", tmp);
                Assert.True(Directory.Exists(proj.CarvedDir));
                proj.Note("hello");
                Assert.True(File.Exists(Path.Combine(proj.Root, "activity.log")));
                Assert.Single(ForensicProject.List(tmp));

                var usn = UsnJournal.Read(vol);
                output.WriteLine($"USN records: {usn.Count}");
                var slack = NtfsForensics.ScanSlack(src, maxFiles: 2000);
                output.WriteLine($"slack entries: {slack.Count}");
                var ils = NtfsForensics.Ils(vol, onlyDeleted: true).Take(5).ToList();
                Assert.NotEmpty(ils);
            }
            finally { Directory.Delete(tmp, true); }
        }
    }
}
