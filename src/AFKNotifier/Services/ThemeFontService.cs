using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace AFKNotifier.Services;

public static class ThemeFontService
{
    private const string FontFamilyName = "IBM 3270";

    public static void ApplyWebsiteFont()
    {
        var fallback = new FontFamily("Consolas");
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "3270-Regular.ttf");

        if (!File.Exists(fontPath))
        {
            Application.Current.Resources["AppFontFamily"] = fallback;
            return;
        }

        try
        {
            var fontDirectory = Path.GetDirectoryName(fontPath)! + Path.DirectorySeparatorChar;
            var fontDirectoryUri = new Uri(fontDirectory, UriKind.Absolute);
            Application.Current.Resources["AppFontFamily"] = new FontFamily(fontDirectoryUri, $"./#{FontFamilyName}");
        }
        catch
        {
            Application.Current.Resources["AppFontFamily"] = fallback;
        }
    }
}
