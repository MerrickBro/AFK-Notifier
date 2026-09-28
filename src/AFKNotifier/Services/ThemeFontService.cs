using System.IO;
using System.Windows;
using System.Windows.Media;

namespace AFKNotifier.Services;

public static class ThemeFontService
{
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
            var directory = Path.GetDirectoryName(fontPath)! + Path.DirectorySeparatorChar;
            var family = new FontFamily(new Uri(directory, UriKind.Absolute), "./#3270");
            Application.Current.Resources["AppFontFamily"] = family;
        }
        catch
        {
            Application.Current.Resources["AppFontFamily"] = fallback;
        }
    }
}
