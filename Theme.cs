using System.IO;
using System.Windows;
using System.Windows.Media;

namespace ProjectOpti;

/// <summary>Accent color switching. The choice is saved to Desktop\Project opti\settings.txt.</summary>
public static class Theme
{
    public static readonly (string Name, string Color, string Hover)[] Accents =
    {
        ("Violet",  "#8B5CF6", "#A78BFA"),
        ("Blue",    "#5B8CFF", "#7AA0FF"),
        ("Emerald", "#10B981", "#34D399"),
        ("Rose",    "#F43F5E", "#FB7185"),
    };

    public static string Current { get; private set; } = "Violet";

    private static string SettingsFile => Path.Combine(AppPaths.Root, "settings.txt");

    public static void ApplySaved()
    {
        string name = "Violet";
        try
        {
            if (File.Exists(SettingsFile))
            {
                foreach (var line in File.ReadAllLines(SettingsFile))
                    if (line.StartsWith("accent=", StringComparison.OrdinalIgnoreCase))
                        name = line["accent=".Length..].Trim();
            }
        }
        catch { /* use default */ }
        Apply(name, save: false);
    }

    public static void Apply(string name, bool save = true)
    {
        var accent = Accents.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (accent.Name == null) accent = Accents[0];

        Application.Current.Resources["AccentBrush"] = Brush(accent.Color);
        Application.Current.Resources["AccentHoverBrush"] = Brush(accent.Hover);
        Current = accent.Name;

        if (!save) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(SettingsFile, $"accent={accent.Name}{Environment.NewLine}");
        }
        catch { /* not critical */ }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
