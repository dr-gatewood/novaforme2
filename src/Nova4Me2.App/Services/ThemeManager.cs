using System.Windows;

namespace Nova4Me2.App.Services;

public static class ThemeManager
{
    public static readonly (string Key, string Name, string Blurb)[] Themes =
    {
        ("NovaDark", "Nova Dark", "Deep charcoal with electric blue accents (default)."),
        ("Midnight", "Midnight", "Navy blues with cyan highlights."),
        ("Graphite", "Graphite", "Neutral greys with warm amber accents."),
        ("AuroraLight", "Aurora Light", "Bright, airy light theme."),
    };

    public static string Current { get; private set; } = "NovaDark";

    public static void Apply(string key)
    {
        if (!Themes.Any(t => t.Key == key)) key = "NovaDark";
        var dict = new ResourceDictionary { Source = new Uri($"Themes/{key}.xaml", UriKind.Relative) };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict; else merged.Insert(0, dict);
        Current = key;
    }
}
