using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace AFKNotifier.Services;

public static class ThemeFontService
{
    private const uint FrPrivate = 0x10;
    private const string FontFamilyName = "IBM 3270";

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceEx(string fileName, uint flags, IntPtr reserved);

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
            var loadedFonts = AddFontResourceEx(fontPath, FrPrivate, IntPtr.Zero);
            Application.Current.Resources["AppFontFamily"] = loadedFonts > 0
                ? new FontFamily(FontFamilyName)
                : fallback;
        }
        catch
        {
            Application.Current.Resources["AppFontFamily"] = fallback;
        }
    }
}
