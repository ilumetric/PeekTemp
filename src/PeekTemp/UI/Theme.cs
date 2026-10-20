using System.Drawing;
using Microsoft.Win32;

namespace PeekTemp.UI;

/// <summary>Colors matching the Windows 11 taskbar (follows the system, not the app, light/dark mode).</summary>
internal sealed class Theme
{
    public required bool Dark { get; init; }

    // Widget
    public required Color Text { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color TextTertiary { get; init; }
    public required Color Warm { get; init; }
    public required Color Hot { get; init; }
    public required Color Fill { get; init; }
    public required Color FillHover { get; init; }
    public required Color FillPressed { get; init; }
    public required Color Stroke { get; init; }

    // Flyout
    public required Color FlyoutBackground { get; init; }
    public required Color FlyoutStroke { get; init; }
    public required Color Divider { get; init; }
    public required Color GraphTemp { get; init; }
    public required Color GraphLoad { get; init; }
    public required Color GraphGrid { get; init; }

    public static readonly Theme DarkTheme = new()
    {
        Dark = true,
        Text = Color.FromArgb(255, 255, 255),
        // Opaque on purpose: GDI+ blends semi-transparent text badly on a layered (alpha) surface.
        TextSecondary = Color.FromArgb(219, 219, 219),
        TextTertiary = Color.FromArgb(160, 160, 160),
        Warm = Color.FromArgb(255, 200, 61),
        Hot = Color.FromArgb(255, 112, 102),
        Fill = Color.FromArgb(15, 255, 255, 255),
        FillHover = Color.FromArgb(26, 255, 255, 255),
        FillPressed = Color.FromArgb(10, 255, 255, 255),
        Stroke = Color.FromArgb(20, 255, 255, 255),

        FlyoutBackground = Color.FromArgb(250, 40, 40, 40),
        FlyoutStroke = Color.FromArgb(30, 255, 255, 255),
        Divider = Color.FromArgb(22, 255, 255, 255),
        GraphTemp = Color.FromArgb(255, 160, 92),
        GraphLoad = Color.FromArgb(96, 205, 255),
        GraphGrid = Color.FromArgb(9, 255, 255, 255),
    };

    public static readonly Theme LightTheme = new()
    {
        Dark = false,
        Text = Color.FromArgb(27, 27, 27),
        TextSecondary = Color.FromArgb(52, 52, 52),
        TextTertiary = Color.FromArgb(104, 104, 104),
        Warm = Color.FromArgb(157, 93, 0),
        Hot = Color.FromArgb(196, 43, 28),
        Fill = Color.FromArgb(150, 255, 255, 255),
        FillHover = Color.FromArgb(215, 255, 255, 255),
        FillPressed = Color.FromArgb(110, 255, 255, 255),
        Stroke = Color.FromArgb(18, 0, 0, 0),

        FlyoutBackground = Color.FromArgb(252, 249, 249, 249),
        FlyoutStroke = Color.FromArgb(26, 0, 0, 0),
        Divider = Color.FromArgb(20, 0, 0, 0),
        GraphTemp = Color.FromArgb(214, 96, 0),
        GraphLoad = Color.FromArgb(0, 103, 192),
        GraphGrid = Color.FromArgb(9, 0, 0, 0),
    };

    public static Theme Current => IsSystemDark() ? DarkTheme : LightTheme;

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int light || light == 0;
        }
        catch
        {
            return true;
        }
    }

    public Color TemperatureColor(float? celsius, double warm, double hot, bool colorize)
    {
        if (!colorize || celsius is not { } t) return Text;
        if (t >= hot) return Hot;
        if (t >= warm) return Warm;
        return Text;
    }
}
