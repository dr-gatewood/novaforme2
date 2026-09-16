using Nova4Me2.Core.Analysis;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Hardware;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Reports;
using Xunit;
using Xunit.Abstractions;

namespace Nova4Me2.Tests;

public class AnalysisTests(ITestOutputHelper output)
{
    private static bool Available => TestImages.Dir != null;

    [Fact]
    public void Health_FlagsDamagedBootSectorAndRecommendsRestore()
    {
        if (!Available) return;
        using var dev = ResilientBlockDevice.ForImage(TestImages.DamagedBoot);
        var h = HealthAnalyzer.Analyze(dev);
        Assert.Single(h.Volumes);
        var v = h.Volumes[0];
        Assert.False(v.PrimaryBootOk); Assert.True(v.BackupBootOk); Assert.True(v.MftOk); Assert.True(v.RootListable);
        Assert.Contains(h.AllFindings, f => f.Repair == RepairKind.RestoreBootSectorFromBackup);
        Assert.True(h.BootFix.LikelihoodPercent >= 80, $"likelihood {h.BootFix.LikelihoodPercent}");
        Assert.Contains(RepairKind.RestoreBootSectorFromBackup, h.BootFix.Recommended);
        output.WriteLine($"score={h.Score} grade={h.Grade} bootfix={h.BootFix.LikelihoodPercent}% verdict={h.BootFix.Verdict}");
    }

    [Fact]
    public void Health_HealthyImageScoresHigh()
    {
        if (!Available) return;
        using var dev = ResilientBlockDevice.ForImage(TestImages.Gpt);
        var h = HealthAnalyzer.Analyze(dev);
        Assert.True(h.Score >= 85, $"score {h.Score}: " + string.Join(" | ", h.AllFindings.Where(f => f.Severity >= Severity.Warning)));
        Assert.Equal("Good", h.Grade);
        Assert.True(h.BootFix.LikelihoodPercent <= 30);
        Assert.True(h.Volumes[0].BootSectorsIdentical);
        Assert.True(h.Volumes[0].MftMirrorMatches);
    }

    [Fact]
    public void Repair_RestoresBootSectorAndGpt_WithUndo()
    {
        if (!Available) return;
        string tmp = Path.Combine(Path.GetTempPath(), "nova-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string img = Path.Combine(tmp, "boot.img");
            File.Copy(TestImages.DamagedBoot, img);
            using (var dev = ResilientBlockDevice.ForImage(img, writable: true))
            {
                var c = VolumeLocator.Find(dev)[0];
                Assert.Equal(BootSectorSource.Backup, c.Source);
                var res = RepairEngine.RestoreBootSectorFromBackup(dev, c, tmp);
                Assert.True(res.Success, res.Message);
                Assert.NotNull(res.BackupFile);
                var c2 = VolumeLocator.Find(dev)[0];
                Assert.Equal(BootSectorSource.Primary, c2.Source);
                var undo = RepairEngine.Undo(dev, res.BackupFile!);
                Assert.True(undo.Success);
                Assert.Equal(BootSectorSource.Backup, VolumeLocator.Find(dev)[0].Source);
            }
            string gimg = Path.Combine(tmp, "gpt.img");
            File.Copy(TestImages.DamagedGpt, gimg);
            using (var dev = ResilientBlockDevice.ForImage(gimg, writable: true))
            {
                Assert.True(PartitionTable.Read(dev).UsedBackupGpt);
                var res = RepairEngine.RestoreGptFromBackup(dev, tmp);
                Assert.True(res.Success, res.Message);
                var t = PartitionTable.Read(dev);
                Assert.True(t.PrimaryGptValid);
                Assert.False(t.UsedBackupGpt);
                Assert.Equal(2, t.Partitions.Count);
                Assert.DoesNotContain(t.Problems, p => p.Contains("CRC"));
            }
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Repair_ConvertsDynamicGptAndMbrToBasic_WithUndo()
    {
        if (!Available || !File.Exists(TestImages.DynamicGpt)) return;
        string tmp = Path.Combine(Path.GetTempPath(), "nova-dyn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // Plain disks are not dynamic.
            using (var dev = ResilientBlockDevice.ForImage(TestImages.Gpt))
            {
                var chk = RepairEngine.CheckDynamicDisk(dev);
                Assert.False(chk.IsDynamic);
                Assert.False(RepairEngine.ConvertDynamicToBasic(dev, tmp).Success);
            }
            string gimg = Path.Combine(tmp, "dyn-gpt.img");
            File.Copy(TestImages.DynamicGpt, gimg);
            using (var dev = ResilientBlockDevice.ForImage(gimg, writable: true))
            {
                var before = PartitionTable.Read(dev);
                Assert.Contains(before.Partitions, p => p.TypeGuid == PartitionTable.GptLdmData);
                Assert.Contains(before.Partitions, p => p.TypeGuid == PartitionTable.GptLdmMeta);
                var chk = RepairEngine.CheckDynamicDisk(dev);
                Assert.True(chk.IsDynamic);
                Assert.True(chk.Convertible, chk.Reason);
                Assert.Equal(2, chk.DataPartition!.Index);
                var health = HealthAnalyzer.Analyze(dev);
                Assert.Contains(health.DiskFindings, f => f.Repair == RepairKind.ConvertDynamicToBasic);

                var res = RepairEngine.ConvertDynamicToBasic(dev, tmp);
                Assert.True(res.Success, res.Message);
                var after = PartitionTable.Read(dev);
                Assert.True(after.PrimaryGptValid && after.BackupGptValid);
                Assert.DoesNotContain(after.Problems, p => p.Contains("CRC"));
                var basic = Assert.Single(after.Partitions);
                Assert.Equal(PartitionTable.GptBasicData, basic.TypeGuid);
                Assert.Equal(before.Partitions.First(p => p.TypeGuid == PartitionTable.GptLdmData).StartOffset, basic.StartOffset);
                Assert.Equal(before.Partitions.First(p => p.TypeGuid == PartitionTable.GptLdmData).UniqueGuid, basic.UniqueGuid);
                Assert.False(RepairEngine.CheckDynamicDisk(dev).IsDynamic);
                var vol = VolumeLocator.Find(dev, after)[0];
                Assert.Equal(basic.StartOffset, vol.StartOffset);
                Assert.NotNull(Nova4Me2.Core.Ntfs.NtfsVolume.Open(dev, vol).Resolve(@"Users\Alice\Documents\hello.txt"));

                var undo = RepairEngine.Undo(dev, res.BackupFile!);
                Assert.True(undo.Success);
                Assert.True(RepairEngine.CheckDynamicDisk(dev).IsDynamic);
                Assert.Equal(2, PartitionTable.Read(dev).Partitions.Count);
            }
            string mimg = Path.Combine(tmp, "dyn-mbr.img");
            File.Copy(TestImages.DynamicMbr, mimg);
            using (var dev = ResilientBlockDevice.ForImage(mimg, writable: true))
            {
                var chk = RepairEngine.CheckDynamicDisk(dev);
                Assert.True(chk.IsDynamic);
                Assert.True(chk.Convertible, chk.Reason);
                Assert.Equal(PartitionScheme.Mbr, chk.Scheme);
                var res = RepairEngine.ConvertDynamicToBasic(dev, tmp);
                Assert.True(res.Success, res.Message);
                var t = PartitionTable.Read(dev);
                Assert.Equal(0x07, Assert.Single(t.Partitions).MbrType);
                Assert.NotNull(Nova4Me2.Core.Ntfs.NtfsVolume.Open(dev, VolumeLocator.Find(dev, t)[0]).Resolve(@"Users\Alice\Documents\hello.txt"));
                Assert.True(RepairEngine.Undo(dev, res.BackupFile!).Success);
                Assert.Equal(0x42, PartitionTable.Read(dev).Partitions[0].MbrType);
            }
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Imager_ClonesPartitionWithHashAndBadSectorMap()
    {
        if (!Available) return;
        string tmp = Path.Combine(Path.GetTempPath(), "nova-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            using var src = ResilientBlockDevice.ForImage(TestImages.Gpt);
            var part = PartitionTable.Read(src).Partitions[1];
            string outPath = Path.Combine(tmp, "part.img");
            using (var dst = FileBlockDevice.Create(outPath, part.Length))
            {
                var p = Imager.Run(src, dst, new ImageOptions { StartOffset = part.StartOffset, Length = part.Length, ChunkSize = 1024 * 1024, VerifyTarget = true, ComputeMd5 = true });
                Assert.Equal("Complete", p.Phase);
                Assert.Equal(part.Length, p.BytesDone);
                Assert.Equal(0, p.BadSectorCount);
                Assert.NotNull(p.Sha256); Assert.NotNull(p.Md5);
                Assert.True(p.VerifyOk);
                var (g, b, s, sl, pe) = p.Map!.Summary();
                Assert.Equal(0, b + s + pe);
            }
            using (var cloned = ResilientBlockDevice.ForImage(outPath))
            {
                var vol = Nova4Me2.Core.Ntfs.NtfsVolume.Open(cloned, VolumeLocator.Find(cloned)[0]);
                Assert.NotNull(vol.Resolve(@"Users\Alice\Documents\hello.txt"));
            }
            // Bad-sector handling: a flaky source with unreadable sectors gets zero-filled and mapped (two-pass).
            var data = new byte[8 * 1024 * 1024];
            new Random(5).NextBytes(data);
            var flaky = new FlakyImage(data) { Bad = { 3000, 3001, 9000 } };
            using var rs = new ResilientBlockDevice(flaky, () => new FlakyImage(data) { Bad = { 3000, 3001, 9000 } }, new ResilienceOptions { Keepalive = null, RetriesPerSector = 1 });
            using var mem = FileBlockDevice.Create(Path.Combine(tmp, "flaky.img"), data.Length);
            var pr = Imager.Run(rs, mem, new ImageOptions { ChunkSize = 1024 * 1024, TwoPass = true });
            Assert.Equal(3, pr.BadSectorCount);
            var got = mem.ReadBytes(0, data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                long sector = i / 512;
                byte want = sector is 3000 or 3001 or 9000 ? (byte)0 : data[i];
                if (got[i] != want) Assert.Fail($"mismatch at {i}");
            }
            Assert.Equal(BlockState.Bad, pr.Map!.States[pr.Map.CellOf(3000 * 512)]);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    private sealed class FlakyImage(byte[] data) : IBlockDevice
    {
        public HashSet<long> Bad = new();
        public long Length => data.Length;
        public int SectorSize => 512;
        public string Description => "flaky image";
        public bool CanWrite => false;
        public void ReadExact(long offset, Span<byte> buffer)
        {
            for (long s = offset / 512; s <= (offset + buffer.Length - 1) / 512; s++) if (Bad.Contains(s)) throw new IOException("CRC", 23);
            data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        }
        public void WriteExact(long offset, ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public void Flush() { }
        public void Dispose() { }
    }

    [Fact]
    public void Reports_RenderAllFormats()
    {
        if (!Available) return;
        using var dev = ResilientBlockDevice.ForImage(TestImages.DamagedBoot);
        var h = HealthAnalyzer.Analyze(dev);
        h.Surface = SurfaceScan.Run(dev, new SurfaceScanOptions { ChunkSize = 4 * 1024 * 1024, SampleEvery = 4 });
        var hw = HardwareInfo.ForDevice(dev, TestImages.DamagedBoot);
        var r = ReportBuilder.FromHealth(h, hw);
        string txt = ReportWriter.ToText(r), html = ReportWriter.ToHtml(r);
        var pdf = ReportWriter.ToPdf(r);
        Assert.Contains("Boot record fix likelihood", txt);
        Assert.Contains("RestoreBootSectorFromBackup", string.Join(",", h.AllFindings.Select(f => f.Repair)));
        Assert.Contains("<html", html); Assert.Contains("Primary NTFS boot sector damaged", html);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.Latin1.GetString(pdf, 0, 8));
        Assert.Contains("%%EOF", System.Text.Encoding.Latin1.GetString(pdf, pdf.Length - 8, 8));
        Assert.True(pdf.Length > 4000);
        string dir = Path.Combine(Path.GetTempPath(), "nova-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var ext in new[] { ".txt", ".html", ".pdf" }) { ReportWriter.Save(r, Path.Combine(dir, "report" + ext)); Assert.True(new FileInfo(Path.Combine(dir, "report" + ext)).Length > 1000); }
        }
        finally { Directory.Delete(dir, true); }
        output.WriteLine(txt[..Math.Min(1500, txt.Length)]);
    }

    [Fact]
    public void VhdFooter_AppendsValidFixedVhdAndImageStillOpens()
    {
        if (!Available) return;
        string tmp = Path.Combine(Path.GetTempPath(), "nova-vhd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string img = Path.Combine(tmp, "copy.img");
            File.Copy(TestImages.Plain, img);
            long raw = new FileInfo(img).Length;
            string vhd = VhdFooter.Append(img);
            Assert.EndsWith(".vhd", vhd);
            Assert.True(VhdFooter.HasFooter(vhd));
            Assert.Equal(raw + 512, new FileInfo(vhd).Length);
            var footer = new byte[512];
            using (var fs = File.OpenRead(vhd)) { fs.Seek(-512, SeekOrigin.End); fs.ReadExactly(footer); }
            Assert.Equal("conectix", System.Text.Encoding.ASCII.GetString(footer, 0, 8));
            Assert.Equal(2u, (uint)(footer[60] << 24 | footer[61] << 16 | footer[62] << 8 | footer[63]));
            ulong size = 0; for (int i = 0; i < 8; i++) size = size << 8 | footer[48 + i];
            Assert.Equal((ulong)raw, size);
            uint sum = 0; for (int i = 0; i < 512; i++) if (i < 64 || i >= 68) sum += footer[i];
            uint stored = (uint)(footer[64] << 24 | footer[65] << 16 | footer[66] << 8 | footer[67]);
            Assert.Equal(~sum, stored);
            using (var dev = ResilientBlockDevice.ForImage(vhd))
            {
                Assert.Equal(raw, dev.Length);
                var vol = Nova4Me2.Core.Ntfs.NtfsVolume.Open(dev, VolumeLocator.Find(dev)[0]);
                Assert.NotNull(vol.Resolve(@"Users\Alice\Documents\hello.txt"));
            }
            VhdFooter.Strip(vhd);
            Assert.Equal(raw, new FileInfo(vhd).Length);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void VendorDatabase_IdentifiesSamsung970Evo()
    {
        var v = VendorDatabase.Identify("Samsung SSD 970 EVO 500GB");
        Assert.Equal("Samsung", v.Name);
        var m = VendorDatabase.Lookup("MZ-V7E500BW");
        Assert.NotNull(m);
        Assert.Contains("970 EVO", m!.Family);
        Assert.Equal("2B2QEXE7", m.LatestKnownFirmware);
        Assert.Equal("500 GB", VendorDatabase.CapacityFromModelCode("MZ-V7E500BW"));
        Assert.Equal("1 TB", VendorDatabase.CapacityFromModelCode("MZ-V7E1T0BW"));
        Assert.Equal("Kingston", VendorDatabase.Identify("KINGSTON SNV2S1000G").Name);
        Assert.Equal("Crucial (Micron)", VendorDatabase.Identify("CT1000P3SSD8").Name);
        Assert.Equal("Western Digital", VendorDatabase.Identify("WDS500G2B0C-00PXH0").Name);
    }
}
