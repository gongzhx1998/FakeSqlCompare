using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace FakeSqlCompare;

public static class ThemeService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FakeSqlCompare", "theme.txt");

    public static string Current { get; private set; } = "Dark";

    public static void Initialize()
    {
        Apply(Load(), persist: false);
    }

    public static string Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var t = File.ReadAllText(FilePath).Trim();
                if (t is "Light" or "Dark") return t;
            }
        }
        catch { /* keep default */ }

        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0);
            if (v is int i && i == 1) return "Light";
        }
        catch { /* ignore */ }

        return "Dark";
    }

    public static void Toggle() => Apply(Current == "Dark" ? "Light" : "Dark");

    public static void Apply(string theme, bool persist = true)
    {
        Current = theme == "Light" ? "Light" : "Dark";
        var uri = new Uri(Current == "Light" ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict;

        if (persist)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, Current);
            }
            catch { /* ignore */ }
        }
    }
}
