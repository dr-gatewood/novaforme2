using System.Text;

namespace Nova4Me2.Core.Util;

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
    }

    public static string Rate(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}.{t.Milliseconds / 100}s";
    }

    public static string Hex(ReadOnlySpan<byte> data, int max = 64)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length && i < max; i++) sb.Append(data[i].ToString("x2"));
        if (data.Length > max) sb.Append('…');
        return sb.ToString();
    }

    public static DateTime FromFileTime(ulong ft)
    {
        try { return ft == 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc((long)ft); }
        catch { return DateTime.MinValue; }
    }
}
