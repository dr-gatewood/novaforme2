using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Util;

namespace Nova4Me2.Core.Forensics;

public sealed class AdsEntry
{
    public NtfsEntry File { get; init; } = null!;
    public string StreamName { get; init; } = "";
    public long Size { get; init; }
    public string Content { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? ExtractedPath { get; set; }
    public string Display => $"{File.Path}:{StreamName}";
    public string SuggestedFileName => Extractor.SafeName(File.Path.Replace('\\', '_') + "__" + StreamName + (KnownExtension is { Length: > 0 } e ? "." + e : ".bin"));
    public string? KnownExtension { get; init; }
}

/// <summary>Finds every named $DATA stream (alternate data stream) on the volume and classifies its content.</summary>
public static class AdsScanner
{
    public static List<AdsEntry> Scan(IDirectorySource src, IProgress<(int Files, int Found)>? progress = null, CancellationToken ct = default, bool includeMetaFiles = false)
    {
        var vol = src.Volume;
        var results = new List<AdsEntry>();
        var stack = new Stack<NtfsEntry>();
        stack.Push(src.Root);
        int files = 0;
        var visited = new HashSet<long>();
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (!visited.Add(dir.Record)) continue;
            List<NtfsEntry> kids;
            try { kids = src.List(dir); } catch { continue; }
            foreach (var e in kids)
            {
                if (e.IsMetaFile && !includeMetaFiles) continue;
                if (e.IsDirectory && !e.IsReparsePoint && e.Record != MftIndex.OrphanRecord) stack.Push(e);
                files++;
                List<(string Name, long Size, NtfsAttribute Attr)> streams;
                try { streams = vol.ListStreams(e.Record); } catch { continue; }
                foreach (var (name, size, _) in streams)
                {
                    if (name.Length == 0) continue;
                    string content = "", ext = "";
                    try
                    {
                        using var s = vol.OpenFile(e, name);
                        var head = new byte[(int)Math.Min(512, s.Length)];
                        int got = 0; while (got < head.Length) { int r = s.Read(head, got, head.Length - got); if (r <= 0) break; got += r; }
                        content = FileCarver.DescribeContent(head.AsSpan(0, got));
                        ext = FileCarver.Identify(head.AsSpan(0, got))?.Extension ?? (content == "text" ? "txt" : "");
                    }
                    catch (Exception ex) { content = "unreadable: " + ex.Message; }
                    results.Add(new AdsEntry { File = e, StreamName = name, Size = size, Content = content, Kind = Classify(name, content, size), KnownExtension = ext });
                }
                if (files % 500 == 0) progress?.Report((files, results.Count));
            }
        }
        progress?.Report((files, results.Count));
        return results.OrderByDescending(r => r.Size).ToList();
    }

    public static string Classify(string streamName, string content, long size)
    {
        if (streamName.Equals("Zone.Identifier", StringComparison.OrdinalIgnoreCase)) return "Mark of the Web (download origin)";
        if (streamName.Equals("SmartScreen", StringComparison.OrdinalIgnoreCase)) return "SmartScreen verdict";
        if (streamName.Equals("encryptable", StringComparison.OrdinalIgnoreCase)) return "Thumbnail cache marker";
        if (streamName.StartsWith("{", StringComparison.Ordinal)) return "System (GUID-named)";
        if (streamName.Equals("$I30", StringComparison.OrdinalIgnoreCase)) return "Directory index";
        if (content.StartsWith("text")) return size > 64 * 1024 ? "Large hidden text" : "Text";
        if (content.Contains("high entropy")) return "Hidden encrypted/compressed data";
        if (content is "empty") return "Empty";
        if (content is "binary") return "Hidden binary data";
        return "Hidden file: " + content;
    }

    public static string Extract(NtfsVolume vol, AdsEntry a, string destDir)
    {
        Directory.CreateDirectory(destDir);
        string path = ForensicProject.UniquePath(destDir, a.SuggestedFileName);
        using var s = vol.OpenFile(a.File, a.StreamName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        s.CopyTo(fs);
        a.ExtractedPath = path;
        return path;
    }
}
