using Nova4Me2.Core.Analysis;

namespace Nova4Me2.Core.Reports;

public sealed class ReportSection
{
    public string Title { get; set; } = "";
    public List<(string Key, string Value)> Rows { get; } = new();
    public List<Finding> Findings { get; } = new();
    public List<string> Paragraphs { get; } = new();
    public List<string[]> Table { get; } = new(); // first row = header
    public List<string> Bullets { get; } = new();
}

public sealed class Report
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public DateTime When { get; set; } = DateTime.Now;
    public string Summary { get; set; } = "";
    public int? Score { get; set; }
    public string Grade { get; set; } = "";
    public int? BootFixLikelihood { get; set; }
    public List<ReportSection> Sections { get; } = new();
    public string Generator => "Nova4Me2 Drive Recovery " + (typeof(Report).Assembly.GetName().Version?.ToString(3) ?? "");

    public ReportSection Add(string title)
    {
        var s = new ReportSection { Title = title };
        Sections.Add(s);
        return s;
    }
}
