using System.Security.Cryptography;
using Nova4Me2.Core.Devices;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Partitions;
using Nova4Me2.Core.Recovery;
using Xunit;
using Xunit.Abstractions;

namespace Nova4Me2.Tests;

public class NtfsImageTests(ITestOutputHelper output)
{
    private static bool Available => TestImages.Dir != null;

    private static (ResilientBlockDevice dev, NtfsVolume vol, NtfsVolumeCandidate cand) OpenPlain(string path)
    {
        var dev = ResilientBlockDevice.ForImage(path);
        var cands = VolumeLocator.Find(dev);
        Assert.Single(cands);
        var vol = NtfsVolume.Open(dev, cands[0]);
        return (dev, vol, cands[0]);
    }

    private static string Sha256(Stream s)
    {
        using var h = SHA256.Create();
        return Convert.ToHexString(h.ComputeHash(s)).ToLowerInvariant();
    }

    [Fact]
    public void Gpt_ParsesPrimaryAndRecoversFromBackup()
    {
        if (!Available) { output.WriteLine("test images unavailable"); return; }
        using var dev = ResilientBlockDevice.ForImage(TestImages.Gpt);
        var t = PartitionTable.Read(dev);
        Assert.Equal(PartitionScheme.Gpt, t.Scheme);
        Assert.Equal(2, t.Partitions.Count);
        Assert.Equal("EFI System", t.Partitions[0].TypeName);
        Assert.Equal("Basic data", t.Partitions[1].TypeName);
        Assert.Equal(33 * 1024 * 1024, t.Partitions[1].StartOffset);
        Assert.True(t.PrimaryGptValid && t.BackupGptValid);
        Assert.False(t.UsedBackupGpt);

        using var dev2 = ResilientBlockDevice.ForImage(TestImages.DamagedGpt);
        var t2 = PartitionTable.Read(dev2);
        Assert.Equal(PartitionScheme.Gpt, t2.Scheme);
        Assert.True(t2.UsedBackupGpt);
        Assert.Equal(2, t2.Partitions.Count);
        Assert.Equal(t.Partitions[1].StartOffset, t2.Partitions[1].StartOffset);
        Assert.Equal(t.Partitions[1].Length, t2.Partitions[1].Length);
        var cands = VolumeLocator.Find(dev2);
        Assert.Single(cands);
        var vol = NtfsVolume.Open(dev2, cands[0]);
        Assert.Equal("NovaTest", vol.Info.Label);
        Assert.NotNull(vol.Resolve(@"Users\Alice\Documents\hello.txt"));
    }

    [Fact]
    public void Volume_OpensViaBackupBootSector()
    {
        if (!Available) return;
        using var dev = ResilientBlockDevice.ForImage(TestImages.DamagedBoot);
        var cands = VolumeLocator.Find(dev);
        Assert.Single(cands);
        Assert.Equal(BootSectorSource.Backup, cands[0].Source);
        Assert.False(cands[0].PrimaryBootSectorValid);
        Assert.Equal(0, cands[0].StartOffset);
        var vol = NtfsVolume.Open(dev, cands[0]);
        var e = vol.Resolve(@"Users\Alice\Documents\hello.txt");
        Assert.NotNull(e);
        using var s = vol.OpenFile(e!);
        Assert.Equal("hello world\n", new StreamReader(s).ReadToEnd());
    }

    [Fact]
    public void Volume_ListsDirectoriesAndResolvesPaths()
    {
        if (!Available) return;
        var (dev, vol, cand) = OpenPlain(TestImages.Plain);
        using (dev)
        {
            Assert.Equal("NovaTest", vol.Info.Label);
            Assert.Equal(BootSectorSource.Primary, cand.Source);
            var root = vol.ListDirectory(NtfsVolume.RootRecord, "");
            var names = root.Select(e => e.Name).ToList();
            Assert.Contains("Users", names); Assert.Contains("Windows", names); Assert.Contains("Program Files", names); Assert.Contains("big", names);
            Assert.Contains("$MFT", names);
            foreach (var d in File.ReadAllLines(Path.Combine(TestImages.Dir!, "dirs.txt")))
            {
                string p = d == "." ? "" : d[2..].Replace('/', '\\');
                if (p.Length == 0) continue;
                var e = vol.Resolve(p);
                Assert.True(e != null && e.IsDirectory, $"directory {p} not found");
            }
            var many = vol.Resolve(@"Users\Bob\Pictures\many")!;
            var kids = vol.ListDirectory(many.Record, many.Path);
            Assert.Equal(600, kids.Count);
            Assert.All(kids, k => Assert.StartsWith(@"Users\Bob\Pictures\many\", k.Path));
            var jp = vol.Resolve(@"Users\Alice\Documents\Projects\日本語ファイル.txt");
            Assert.NotNull(jp);
            Assert.NotNull(vol.Resolve("users/alice/documents/HELLO.TXT"));
        }
    }

    [Fact]
    public void Volume_FileContentsMatchManifest_IncludingCompressedSparseFragmented()
    {
        if (!Available) return;
        var (dev, vol, _) = OpenPlain(TestImages.Plain);
        using (dev)
        {
            var manifest = TestImages.Manifest();
            Assert.True(manifest.Count > 600);
            int compressedSeen = 0, sparseSeen = 0;
            foreach (var (path, hash) in manifest)
            {
                var e = vol.Resolve(path);
                Assert.True(e != null, $"missing {path}");
                using var s = vol.OpenFile(e!);
                if (s.IsCompressed) compressedSeen++;
                if (e!.IsSparse) sparseSeen++;
                Assert.Equal(e.Size, s.Length);
                Assert.True(hash == Sha256(s), $"content mismatch for {path} (compressed={s.IsCompressed}, sparse={e.IsSparse}, size={e.Size})");
            }
            output.WriteLine($"verified {manifest.Count} files, {compressedSeen} compressed, {sparseSeen} sparse");
            Assert.True(compressedSeen >= 3, "expected LZNT1-compressed files in the image");
            Assert.True(sparseSeen >= 1, "expected a sparse file in the image");
            var frag = vol.Resolve(@"big\frag-a.bin")!;
            var rec = vol.GetRecord(frag.Record);
            var data = vol.FindAttribute(rec, AttrType.Data)!;
            output.WriteLine($"frag-a.bin has {data.Runs.Count} runs");
            Assert.True(data.Runs.Count > 5, "expected a fragmented file");
        }
    }

    [Fact]
    public void Volume_RandomAccessReadsMatchSequential()
    {
        if (!Available) return;
        var (dev, vol, _) = OpenPlain(TestImages.Plain);
        using (dev)
        {
            foreach (var path in new[] { @"big\random-20MiB.bin", @"Users\Alice\Compressed\text-2MiB.txt", @"big\sparse-12MiB.bin", @"Users\Alice\Compressed\mixed-1MiB.bin" })
            {
                var e = vol.Resolve(path)!;
                using var s = vol.OpenFile(e);
                var all = new byte[s.Length];
                int t = 0; while (t < all.Length) { int n = s.Read(all, t, all.Length - t); if (n <= 0) break; t += n; }
                Assert.Equal(all.Length, t);
                var rnd = new Random(7);
                for (int i = 0; i < 50; i++)
                {
                    long off = rnd.NextInt64(0, s.Length);
                    int len = (int)Math.Min(rnd.Next(1, 70000), s.Length - off);
                    var buf = new byte[len];
                    s.Position = off;
                    int got = 0; while (got < len) { int n = s.Read(buf, got, len - got); if (n <= 0) break; got += n; }
                    Assert.Equal(len, got);
                    Assert.True(all.AsSpan((int)off, len).SequenceEqual(buf), $"random read mismatch in {path} at {off}+{len}");
                }
            }
        }
    }

    [Fact]
    public void Streams_HardLinks_AndExtractor()
    {
        if (!Available) return;
        var (dev, vol, _) = OpenPlain(TestImages.Plain);
        using (dev)
        {
            var hello = vol.Resolve(@"Users\Alice\Documents\hello.txt")!;
            var streams = vol.ListStreams(hello.Record);
            Assert.Contains(streams, s => s.Name == "secret");
            using (var ads = vol.OpenFile(hello, "secret")) Assert.Equal("ads payload\n", new StreamReader(ads).ReadToEnd());
            var link = vol.Resolve(@"Users\Bob\hello-link.txt")!;
            Assert.Equal(hello.Record, link.Record);

            string dest = Path.Combine(Path.GetTempPath(), "nova4me2-extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                var ex = new Extractor(new IndexDirectorySource(vol));
                var users = vol.Resolve("Users")!;
                var prog = ex.Copy(new[] { users, vol.Resolve(@"big\frag-a.bin")! }, dest, new CopyOptions { CopyAlternateStreams = true, VerifyAfterCopy = true });
                Assert.Equal(0, prog.FilesFailed);
                Assert.True(prog.FilesDone > 600);
                var manifest = TestImages.Manifest();
                foreach (var (path, hash) in manifest)
                {
                    if (!path.StartsWith(@"Users\") && path != @"big\frag-a.bin") continue;
                    string local = Path.Combine(dest, path.StartsWith(@"big\") ? path[(path.LastIndexOf('\\') + 1)..] : path.Replace('\\', Path.DirectorySeparatorChar));
                    Assert.True(File.Exists(local), $"not extracted: {path}");
                    using var fs = File.OpenRead(local);
                    Assert.Equal(hash, Sha256(fs));
                }
                if (!OperatingSystem.IsWindows()) Assert.True(File.Exists(Path.Combine(dest, "Users", "Alice", "Documents", "hello.txt~secret.ads")));
                var mtime = File.GetLastWriteTimeUtc(Path.Combine(dest, "Users", "Alice", "Documents", "hello.txt"));
                Assert.True(Math.Abs((mtime - hello.Modified).TotalSeconds) < 2);
            }
            finally { try { Directory.Delete(dest, true); } catch { } }
        }
    }

    [Fact]
    public void MftScan_RebuildsTreeAndFindsDeletedFile()
    {
        if (!Available) return;
        var (dev, vol, _) = OpenPlain(TestImages.Plain);
        using (dev)
        {
            var idx = MftScanner.Scan(vol, includeDeleted: true);
            output.WriteLine($"MFT: {idx.TotalRecords} records, {idx.InUseRecords} in use, {idx.DeletedRecords} deleted, {idx.FileCount} files, {idx.DirectoryCount} dirs in {idx.Elapsed.TotalMilliseconds:0} ms");
            var src = new MftIndexDirectorySource(vol, idx);
            var manifest = TestImages.Manifest();
            foreach (var (path, hash) in manifest)
            {
                var parts = path.Split('\\');
                var cur = src.Root;
                foreach (var part in parts)
                {
                    var next = src.List(cur).FirstOrDefault(e => e.Name == part && !e.IsDeleted);
                    Assert.True(next != null, $"MFT-scan tree missing {path} at '{part}'");
                    cur = next!;
                }
                using var s = vol.OpenFile(cur);
                Assert.Equal(hash, Sha256(s));
            }
            var bob = src.List(src.List(src.Root).First(e => e.Name == "Users")).First(e => e.Name == "Bob");
            var deleted = src.List(bob).FirstOrDefault(e => e.Name == "deleted-me.bin");
            Assert.True(deleted != null && deleted.IsDeleted, "deleted file not found by MFT scan");
            using var ds = vol.OpenFile(deleted!);
            var expected = File.ReadAllBytes(Path.Combine(TestImages.Dir!, "deleted-me.expected"));
            Assert.Equal(expected.Length, ds.Length);
            var got = new byte[ds.Length];
            int t = 0; while (t < got.Length) { int n = ds.Read(got, t, got.Length - t); if (n <= 0) break; t += n; }
            Assert.Equal(expected, got);
        }
    }
}
