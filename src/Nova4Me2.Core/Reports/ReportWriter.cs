using System.Globalization;
using System.Net;
using System.Text;
using Nova4Me2.Core.Analysis;

namespace Nova4Me2.Core.Reports;

public static class ReportWriter
{
    public static void Save(Report r, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".html": case ".htm": File.WriteAllText(path, ToHtml(r), Encoding.UTF8); break;
            case ".pdf": File.WriteAllBytes(path, ToPdf(r)); break;
            default: File.WriteAllText(path, ToText(r), Encoding.UTF8); break;
        }
    }

    // ---------------------------------------------------------------- TXT
    public static string ToText(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Title.ToUpperInvariant());
        sb.AppendLine(new string('=', r.Title.Length));
        if (r.Subtitle.Length > 0) sb.AppendLine(r.Subtitle);
        sb.AppendLine($"Generated {r.When:yyyy-MM-dd HH:mm:ss} by {r.Generator}");
        if (r.Score != null) sb.AppendLine($"Health score: {r.Score}/100 ({r.Grade})");
        if (r.BootFixLikelihood != null) sb.AppendLine($"Boot record fix likelihood: {r.BootFixLikelihood}%");
        sb.AppendLine();
        if (r.Summary.Length > 0) { sb.AppendLine(Wrap(r.Summary, 100)); sb.AppendLine(); }
        foreach (var s in r.Sections)
        {
            sb.AppendLine(s.Title);
            sb.AppendLine(new string('-', s.Title.Length));
            int w = s.Rows.Count > 0 ? Math.Min(40, s.Rows.Max(x => x.Key.Length)) : 0;
            foreach (var (k, v) in s.Rows) sb.AppendLine($"  {k.PadRight(w)} : {v}");
            foreach (var p in s.Paragraphs) { sb.AppendLine(); sb.AppendLine(Wrap(p, 100, "  ")); }
            foreach (var b in s.Bullets) sb.AppendLine(Wrap("- " + b, 100, "  ", "    "));
            if (s.Table.Count > 0) { sb.AppendLine(); AppendTable(sb, s.Table); }
            foreach (var f in s.Findings)
            {
                sb.AppendLine();
                sb.AppendLine($"  [{f.Severity.ToString().ToUpperInvariant()}] {f.Title}");
                sb.AppendLine(Wrap(f.Detail, 100, "      "));
                if (f.Repair != RepairKind.None) sb.AppendLine(Wrap("Fix: " + RepairKindText.Title(f.Repair) + ". " + RepairKindText.Explain(f.Repair), 100, "      "));
                if (f.Advice != null) sb.AppendLine(Wrap("Advice: " + f.Advice, 100, "      "));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, List<string[]> t)
    {
        int cols = t.Max(r => r.Length);
        var w = new int[cols];
        foreach (var row in t) for (int i = 0; i < row.Length; i++) w[i] = Math.Min(60, Math.Max(w[i], row[i].Length));
        for (int ri = 0; ri < t.Count; ri++)
        {
            var row = t[ri];
            sb.Append("  ");
            for (int i = 0; i < cols; i++) sb.Append((i < row.Length ? Trunc(row[i], w[i]) : "").PadRight(w[i])).Append(i < cols - 1 ? "  " : "");
            sb.AppendLine();
            if (ri == 0) { sb.Append("  "); for (int i = 0; i < cols; i++) sb.Append(new string('-', w[i])).Append(i < cols - 1 ? "  " : ""); sb.AppendLine(); }
        }
    }

    private static string Trunc(string s, int w) => s.Length <= w ? s : s[..(w - 1)] + "…";

    public static string Wrap(string text, int width, string indent = "", string? subsequent = null)
    {
        subsequent ??= indent;
        var sb = new StringBuilder();
        foreach (var para in text.Split('\n'))
        {
            var line = new StringBuilder(indent);
            bool first = true;
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length + word.Length + 1 > width && line.Length > (first ? indent : subsequent).Length)
                {
                    sb.AppendLine(line.ToString().TrimEnd());
                    line.Clear().Append(subsequent);
                    first = false;
                }
                line.Append(word).Append(' ');
            }
            sb.AppendLine(line.ToString().TrimEnd());
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    // ---------------------------------------------------------------- HTML
    public static string ToHtml(Report r)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>").Append(E(r.Title)).Append("</title><style>");
        sb.Append(@"
:root{--bg:#0f1218;--panel:#161b24;--panel2:#1c2330;--text:#e6e9ef;--muted:#9aa4b5;--accent:#6ea8fe;--good:#3ddc97;--warn:#f5c04a;--err:#ff6b6b;--crit:#ff3d71;--info:#8fa3c4;--line:#273040}
@media (prefers-color-scheme: light){:root{--bg:#f5f7fb;--panel:#fff;--panel2:#eef2f8;--text:#1b2230;--muted:#5b667a;--line:#d9e0ea}}
*{box-sizing:border-box}body{margin:0;font:15px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;background:var(--bg);color:var(--text);padding:32px 18px}
.wrap{max-width:1080px;margin:0 auto}header{display:flex;flex-wrap:wrap;gap:20px;align-items:center;margin-bottom:24px}
.logo{width:56px;height:56px;border-radius:14px;background:radial-gradient(circle at 30% 30%,#8fd3ff,#3b6fe0 60%,#1b2a5a);box-shadow:0 0 30px #3b6fe088;position:relative}
.logo:after{content:'';position:absolute;inset:16px;border-radius:50%;background:conic-gradient(from 0deg,#fff,#8fd3ff,#fff);opacity:.9}
h1{font-size:26px;margin:0}h2{font-size:18px;margin:32px 0 10px;padding-bottom:6px;border-bottom:1px solid var(--line)}.sub{color:var(--muted)}
.cards{display:flex;flex-wrap:wrap;gap:14px;margin:18px 0}.card{flex:1 1 200px;background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:16px}
.card .big{font-size:34px;font-weight:700}.summary{background:var(--panel2);border-left:4px solid var(--accent);padding:14px 16px;border-radius:10px}
table{border-collapse:collapse;width:100%;background:var(--panel);border:1px solid var(--line);border-radius:10px;overflow:hidden;font-size:14px}
th,td{padding:8px 10px;text-align:left;vertical-align:top;border-bottom:1px solid var(--line)}th{background:var(--panel2);color:var(--muted);font-weight:600}tr:last-child td{border-bottom:0}
.kv td:first-child{color:var(--muted);width:32%}.f{border:1px solid var(--line);border-left-width:5px;border-radius:10px;padding:10px 14px;margin:10px 0;background:var(--panel)}
.f .t{font-weight:600}.f .d{color:var(--muted)}.f.Good{border-left-color:var(--good)}.f.Info{border-left-color:var(--info)}.f.Warning{border-left-color:var(--warn)}.f.Error{border-left-color:var(--err)}.f.Critical{border-left-color:var(--crit)}
.pill{display:inline-block;font-size:11px;padding:2px 8px;border-radius:999px;margin-right:8px;text-transform:uppercase;letter-spacing:.5px;background:var(--panel2)}
.fix{margin-top:6px;padding:8px 10px;background:var(--panel2);border-radius:8px}.bar{height:10px;background:var(--panel2);border-radius:999px;overflow:hidden;margin-top:8px}.bar i{display:block;height:100%;background:linear-gradient(90deg,var(--good),var(--accent))}
footer{color:var(--muted);font-size:12px;margin-top:40px}ul{margin:8px 0}@media print{body{background:#fff;color:#000}.card,table,.f{break-inside:avoid}}
");
        sb.Append("</style></head><body><div class=\"wrap\"><header><div class=\"logo\"></div><div><h1>").Append(E(r.Title)).Append("</h1><div class=\"sub\">").Append(E(r.Subtitle)).Append(" · ").Append(r.When.ToString("f", CultureInfo.InvariantCulture)).Append("</div></div></header>");
        if (r.Score != null || r.BootFixLikelihood != null)
        {
            sb.Append("<div class=\"cards\">");
            if (r.Score != null) sb.Append($"<div class=\"card\"><div class=\"sub\">Structural health</div><div class=\"big\">{r.Score}<span class=\"sub\" style=\"font-size:16px\">/100 · {E(r.Grade)}</span></div><div class=\"bar\"><i style=\"width:{r.Score}%\"></i></div></div>");
            if (r.BootFixLikelihood != null) sb.Append($"<div class=\"card\"><div class=\"sub\">Boot record fix likelihood</div><div class=\"big\">{r.BootFixLikelihood}%</div><div class=\"bar\"><i style=\"width:{r.BootFixLikelihood}%\"></i></div></div>");
            sb.Append("</div>");
        }
        if (r.Summary.Length > 0) sb.Append("<div class=\"summary\">").Append(E(r.Summary)).Append("</div>");
        foreach (var s in r.Sections)
        {
            sb.Append("<h2>").Append(E(s.Title)).Append("</h2>");
            if (s.Rows.Count > 0)
            {
                sb.Append("<table class=\"kv\">");
                foreach (var (k, v) in s.Rows) sb.Append("<tr><td>").Append(E(k)).Append("</td><td>").Append(E(v)).Append("</td></tr>");
                sb.Append("</table>");
            }
            foreach (var p in s.Paragraphs) sb.Append("<p>").Append(E(p)).Append("</p>");
            if (s.Bullets.Count > 0) { sb.Append("<ul>"); foreach (var b in s.Bullets) sb.Append("<li>").Append(E(b)).Append("</li>"); sb.Append("</ul>"); }
            if (s.Table.Count > 0)
            {
                sb.Append("<table>");
                for (int i = 0; i < s.Table.Count; i++)
                {
                    sb.Append("<tr>");
                    foreach (var c in s.Table[i]) sb.Append(i == 0 ? "<th>" : "<td>").Append(E(c)).Append(i == 0 ? "</th>" : "</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</table>");
            }
            foreach (var f in s.Findings)
            {
                sb.Append($"<div class=\"f {f.Severity}\"><span class=\"pill\">{f.Severity}</span><span class=\"t\">").Append(E(f.Title)).Append("</span><div class=\"d\">").Append(E(f.Detail)).Append("</div>");
                if (f.Repair != RepairKind.None) sb.Append("<div class=\"fix\"><b>").Append(E(RepairKindText.Title(f.Repair))).Append("</b> — ").Append(E(RepairKindText.Explain(f.Repair))).Append("</div>");
                if (f.Advice != null) sb.Append("<div class=\"fix\">").Append(E(f.Advice)).Append("</div>");
                sb.Append("</div>");
            }
        }
        sb.Append("<footer>Generated by ").Append(E(r.Generator)).Append(". Reads were made directly from the device; nothing was written to it while producing this report.</footer></div></body></html>");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- PDF (minimal, dependency-free)
    public static byte[] ToPdf(Report r)
    {
        var pdf = new PdfWriter();
        pdf.Heading(r.Title, 18);
        if (r.Subtitle.Length > 0) pdf.Text(r.Subtitle, 11, PdfWriter.Font.Regular, 0.35);
        pdf.Text($"Generated {r.When:yyyy-MM-dd HH:mm} by {r.Generator}", 9, PdfWriter.Font.Regular, 0.45);
        if (r.Score != null) pdf.Text($"Structural health: {r.Score}/100 ({r.Grade})", 11, PdfWriter.Font.Bold);
        if (r.BootFixLikelihood != null) pdf.Text($"Boot record fix likelihood: {r.BootFixLikelihood}%", 11, PdfWriter.Font.Bold);
        pdf.Space(6);
        if (r.Summary.Length > 0) { pdf.Paragraph(r.Summary, 10); pdf.Space(6); }
        foreach (var s in r.Sections)
        {
            pdf.Space(8);
            pdf.Heading(s.Title, 13);
            foreach (var (k, v) in s.Rows) pdf.KeyValue(k, v);
            foreach (var p in s.Paragraphs) { pdf.Space(3); pdf.Paragraph(p, 10); }
            foreach (var b in s.Bullets) pdf.Paragraph("• " + b, 10, 12);
            if (s.Table.Count > 0) { pdf.Space(3); pdf.Table(s.Table); }
            foreach (var f in s.Findings)
            {
                pdf.Space(4);
                pdf.Text($"[{f.Severity.ToString().ToUpperInvariant()}] {f.Title}", 10, PdfWriter.Font.Bold);
                pdf.Paragraph(f.Detail, 9.5, 12);
                if (f.Repair != RepairKind.None) pdf.Paragraph("Fix: " + RepairKindText.Title(f.Repair) + ". " + RepairKindText.Explain(f.Repair), 9.5, 12, 0.3);
                if (f.Advice != null) pdf.Paragraph("Advice: " + f.Advice, 9.5, 12, 0.3);
            }
        }
        return pdf.Finish();
    }
}

/// <summary>Tiny PDF 1.4 writer: Helvetica/Courier text with word wrapping, pagination and simple tables. No compression, no images.</summary>
public sealed class PdfWriter
{
    public enum Font { Regular = 1, Bold = 2, Mono = 3 }
    private const double PageW = 612, PageH = 792, Margin = 54;
    private readonly List<StringBuilder> _pages = new();
    private StringBuilder _cur = null!;
    private double _y;

    public PdfWriter() { NewPage(); }

    private void NewPage() { _cur = new StringBuilder(); _pages.Add(_cur); _y = PageH - Margin; }
    private void Ensure(double h) { if (_y - h < Margin) NewPage(); }

    public void Space(double pt) { _y -= pt; if (_y < Margin) NewPage(); }

    public void Heading(string text, double size) { Space(size * 0.6); Text(text, size, Font.Bold); Space(2); }

    public void Text(string text, double size, Font font = Font.Regular, double gray = 0)
    {
        foreach (var line in WrapText(text, size, font, PageW - 2 * Margin)) Line(line, size, font, Margin, gray);
    }

    public void Paragraph(string text, double size, double indent = 0, double gray = 0)
    {
        foreach (var line in WrapText(text, size, Font.Regular, PageW - 2 * Margin - indent)) Line(line, size, Font.Regular, Margin + indent, gray);
    }

    public void KeyValue(string key, string value)
    {
        double keyW = 170;
        var vl = WrapText(value, 9.5, Font.Regular, PageW - 2 * Margin - keyW - 6).ToList();
        Ensure(12 * Math.Max(1, vl.Count));
        double y0 = _y;
        Line(key, 9.5, Font.Regular, Margin, 0.4);
        _y = y0;
        foreach (var l in vl) Line(l, 9.5, Font.Regular, Margin + keyW, 0);
    }

    public void Table(List<string[]> rows)
    {
        int cols = rows.Max(r => r.Length);
        double total = PageW - 2 * Margin;
        var maxLen = new double[cols];
        foreach (var r in rows) for (int i = 0; i < r.Length; i++) maxLen[i] = Math.Max(maxLen[i], Math.Min(r[i].Length, 60) + 2);
        double sum = maxLen.Sum();
        var w = maxLen.Select(m => Math.Max(40, total * m / sum)).ToArray();
        double ws = w.Sum(); for (int i = 0; i < cols; i++) w[i] = w[i] * total / ws;
        for (int ri = 0; ri < rows.Count; ri++)
        {
            var cells = rows[ri];
            var wrapped = new List<string>[cols];
            int lines = 1;
            for (int i = 0; i < cols; i++) { wrapped[i] = WrapText(i < cells.Length ? cells[i] : "", 8.5, Font.Mono, w[i] - 4).ToList(); lines = Math.Max(lines, wrapped[i].Count); }
            Ensure(lines * 10.5 + 2);
            double y0 = _y;
            double x = Margin;
            for (int i = 0; i < cols; i++)
            {
                _y = y0;
                foreach (var l in wrapped[i]) Line(l, 8.5, ri == 0 ? Font.Bold : Font.Mono, x + 2, ri == 0 ? 0.3 : 0);
                x += w[i];
            }
            _y = y0 - lines * 10.5;
            if (ri == 0) { _cur.Append($"0.6 G {Margin:0.##} {_y + 2:0.##} m {PageW - Margin:0.##} {_y + 2:0.##} l S 0 G\n"); _y -= 2; }
        }
        Space(4);
    }

    private void Line(string text, double size, Font font, double x, double gray)
    {
        Ensure(size * 1.25);
        _y -= size * 1.2;
        _cur.Append($"BT /F{(int)font} {size:0.##} Tf {gray:0.##} g 1 0 0 1 {x:0.##} {_y:0.##} Tm ({Escape(text)}) Tj 0 g ET\n");
    }

    private static IEnumerable<string> WrapText(string text, double size, Font font, double width)
    {
        double cw = font == Font.Mono ? 0.6 : 0.5; // rough average glyph width factor
        int maxChars = Math.Max(8, (int)(width / (size * cw)));
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string wd = word;
                while (wd.Length > maxChars) { if (line.Length > 0) { yield return line.ToString(); line.Clear(); } yield return wd[..maxChars]; wd = wd[maxChars..]; }
                if (line.Length + wd.Length + 1 > maxChars && line.Length > 0) { yield return line.ToString(); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(wd);
            }
            yield return line.ToString();
        }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch == '(' || ch == ')' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 32) sb.Append(' ');
            else if (ch < 127) sb.Append(ch);
            else sb.Append(ch switch { '—' => '-', '–' => '-', '…' => '.', '“' or '”' => '"', '‘' or '’' => '\'', '•' => '*', '°' => 'o', '×' => 'x', '≈' => '~', '·' => '-', '→' => '>', _ => ch <= 255 ? ch : '?' });
        }
        return sb.ToString();
    }

    public byte[] Finish()
    {
        var objs = new List<byte[]>();
        var latin1 = Encoding.Latin1;
        // 1 catalog, 2 pages, 3-5 fonts, then per page: page obj + content obj
        int n = _pages.Count;
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{6 + i * 2} 0 R"));
        objs.Add(latin1.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"));
        objs.Add(latin1.GetBytes($"<< /Type /Pages /Kids [{kids}] /Count {n} >>"));
        objs.Add(latin1.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        objs.Add(latin1.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));
        objs.Add(latin1.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"));
        for (int i = 0; i < n; i++)
        {
            objs.Add(latin1.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageW} {PageH}] /Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> /Contents {7 + i * 2} 0 R >>"));
            var content = latin1.GetBytes(_pages[i].ToString() + $"BT /F1 8 Tf 0.5 g 1 0 0 1 {Margin} 30 Tm (Nova4Me2 - page {i + 1} of {n}) Tj 0 g ET\n");
            var stream = new List<byte>();
            stream.AddRange(latin1.GetBytes($"<< /Length {content.Length} >>\nstream\n"));
            stream.AddRange(content);
            stream.AddRange(latin1.GetBytes("\nendstream"));
            objs.Add(stream.ToArray());
        }
        var ms = new MemoryStream();
        void W(string s) { var b = latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n");
            ms.Write(objs[i], 0, objs[i].Length);
            W("\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:0000000000} 00000 n \n");
        W($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
