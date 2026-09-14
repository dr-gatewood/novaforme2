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

    [Fact]
    public void BadSectorLog_ParsesNovaDdrescueAndLbaFormats()
    {
        var nova = BadSectorLog.Parse("# Nova4Me2 unreadable sector log — 2026-09-14 02:46:04\n# Source: Samsung SSD 970 EVO 500G (465.76 GB, USB)\n# byte_offset\tlength\tLBA\tsectors\n205253541888\t512\t400885824\t1\n205253542400\t512\t400885825\t1\n205254183936\t512\t400887078\t1\n");
        Assert.Equal("Nova4Me2 bad sector log", nova.Format);
        Assert.Equal("Samsung SSD 970 EVO 500G (465.76 GB, USB)", nova.SourceDescription);
        Assert.Equal(2, nova.Ranges.Count); // adjacent sectors coalesce
        Assert.Equal((205253541888L, 1024L), nova.Ranges[0]);
        Assert.Equal(1536, nova.TotalBytes);
        Assert.Equal(3, nova.TotalSectors);

        var dd = BadSectorLog.Parse("# Mapfile. Created by GNU ddrescue version 1.27\n# current_pos  current_status  current_pass\n0x00000000     +               1\n#      pos        size  status\n0x00000000  0x00100000  +\n0x00100000  0x00000200  -\n0x00100200  0x00000400  /\n0x00100600  0x0FF00000  +\n");
        Assert.Equal("ddrescue mapfile", dd.Format);
        Assert.Single(dd.Ranges);
        Assert.Equal((0x100000L, 0x600L), dd.Ranges[0]);

        var lba = BadSectorLog.Parse("100\n101\n300\n", sectorSize: 512);
        Assert.Equal("LBA list", lba.Format);
        Assert.Equal(2, lba.Ranges.Count);
        Assert.Equal((51200L, 1024L), lba.Ranges[0]);
    }

    [Fact]
    public void BadSectors_MapToFileFreeSpaceMftAndSlack()
    {
        if (!Available) return;
        var (dev, vol) = Open();
        using (dev)
        {
            int cs = vol.ClusterSize;
            var bitmap = ClusterBitmap.Load(vol);
            var big = vol.Resolve(@"big\random-20MiB.bin")!;
            var bigData = vol.FindAttribute(vol.GetRecord(big.Record), AttrType.Data)!;
            var run0 = bigData.Runs[0];
            long inBig = vol.Offset + run0.Lcn * (long)cs + cs + 512; // second cluster of the file, +512
            var owners = ClusterOwnerMap.Build(vol);
            long freeLcn = bitmap.UnallocatedRanges().SelectMany(r => Enumerable.Range(0, (int)Math.Min(r.Count, 64)).Select(i => r.Lcn + i)).First(l => owners.Find(l) == null); // free and never referenced by a (deleted) file
            long inFree = vol.Offset + freeLcn * (long)cs;
            var hello = vol.Resolve(@"Users\Alice\Documents\hello.txt")!;
            var mftRun = vol.MftDataAttribute.Runs[0];
            long inMft = vol.Offset + mftRun.Lcn * (long)cs + hello.Record * vol.RecordSize;
            var small = vol.Resolve(@"Users\Alice\Documents\nonresident-small.bin")!; // 5000 bytes: second cluster is mostly slack
            var smallData = vol.FindAttribute(vol.GetRecord(small.Record), AttrType.Data)!;
            long inSlack = vol.Offset + (smallData.Runs[0].Lcn + 1) * (long)cs + 2048; // file offset 6144 > 5000

            var log = BadSectorLog.Parse($"{inBig}\t512\n{inFree}\t512\n{inMft}\t1024\n{inSlack}\t512\n", "unit.badsectors.txt");
            Assert.Equal(4, log.Ranges.Count);
            var report = BadSectorAnalyzer.Analyze(dev, log);
            output.WriteLine(report.ToText());

            var bigFile = Assert.Single(report.Files, f => f.Record == big.Record);
            Assert.Equal(@"\big\random-20MiB.bin", bigFile.Path);
            Assert.Equal(512, bigFile.BytesLost);
            Assert.Equal((run0.Vcn + 1) * cs + 512, bigFile.FirstOffset);
            Assert.Equal(20L * 1024 * 1024, bigFile.FileSize);
            Assert.False(bigFile.Metadata);
            Assert.Contains("small hole", bigFile.Impact);

            var freeHit = Assert.Single(report.Hits, h => h.Offset == inFree);
            Assert.Equal(BadSectorArea.NtfsUnallocated, freeHit.Area);
            Assert.Equal(freeLcn, freeHit.Lcn);
            Assert.Equal(512, report.Bytes(BadSectorArea.NtfsUnallocated));

            var mft = Assert.Single(report.Files, f => f.Record == 0 && f.Attribute == "$DATA");
            Assert.True(mft.Metadata);
            Assert.Contains(mft.DamagedRecords, d => d.Record == hello.Record && d.Name.EndsWith("hello.txt"));

            var slackFile = Assert.Single(report.Files, f => f.Record == small.Record);
            Assert.Equal(0, slackFile.BytesLost);
            Assert.Equal(512, slackFile.BytesInSlack);
            Assert.Equal(BadSectorArea.NtfsSlack, Assert.Single(report.Hits, h => h.Offset == inSlack).Area);
            Assert.Contains("intact", slackFile.Impact);

            Assert.Equal(1, report.UserFilesAffected);
            Assert.Contains("1 file(s) lost", report.Headline);
            Assert.Contains("MFT record", report.Headline);

            string tmp = Path.Combine(Path.GetTempPath(), "nova-bad-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                report.WriteCsv(Path.Combine(tmp, "bad.csv"));
                report.WriteText(Path.Combine(tmp, "bad.txt"));
                Assert.Equal(report.Hits.Count + 1, File.ReadAllLines(Path.Combine(tmp, "bad.csv")).Length);
                Assert.Contains("AFFECTED FILES", File.ReadAllText(Path.Combine(tmp, "bad.txt")));
            }
            finally { Directory.Delete(tmp, true); }

            // A log that only touches free space gives the all-clear.
            var clean = BadSectorAnalyzer.Analyze(dev, BadSectorLog.Parse($"{inFree}\t4096\n"));
            Assert.Equal("No files affected.", clean.Headline);
            Assert.Empty(clean.Files);
        }
    }

    [Fact]
    public void BadSectors_GptImage_ClassifiesTableGapAndForeignPartition()
    {
        if (!Available) return;
        using var dev = ResilientBlockDevice.ForImage(TestImages.Gpt);
        var table = PartitionTable.Read(dev);
        var vols = VolumeLocator.Find(dev, table);
        var ntfs = vols[0];
        var log = BadSectorLog.Parse("512\t512\n" + $"{700 * 1024}\t512\n" + $"{2 * 1024 * 1024}\t512\n" + $"{dev.Length - 512}\t512\n" + $"{ntfs.StartOffset + 3 * 4096}\t512\n");
        var r = BadSectorAnalyzer.Analyze(dev, log, table, vols);
        output.WriteLine(r.ToText());
        Assert.Equal(BadSectorArea.PartitionTable, r.Hits[0].Area);
        Assert.Contains("GPT header", r.Hits[0].AreaText);
        Assert.Equal(BadSectorArea.Unpartitioned, r.Hits[1].Area);
        Assert.Equal(BadSectorArea.NonNtfsPartition, r.Hits[2].Area);
        Assert.Contains("EFI", r.Hits[2].AreaText);
        Assert.Equal(3, r.Hits[3].Lcn); // hits are sorted by offset: the NTFS one precedes the backup GPT at the end of the disk
        Assert.StartsWith("Partition 2", r.Hits[3].Partition);
        Assert.Equal(BadSectorArea.PartitionTable, r.Hits[4].Area);
        Assert.Contains("backup GPT", r.Hits[4].AreaText);
        Assert.Contains("Partition-table sectors are affected", r.Verdict);
    }
}
