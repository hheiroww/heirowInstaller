using Microsoft.Xna.Framework;
using ProjectZ.Shared.Content;

namespace HeirowInstaller.App;

internal static class Theme
{
    public static readonly Color Background = new(12, 17, 25);
    public static readonly Color Rail = new(15, 21, 30);
    public static readonly Color Surface = new(20, 27, 38);
    public static readonly Color SurfaceRaised = new(25, 34, 47);
    public static readonly Color Field = new(11, 17, 26);
    public static readonly Color Border = new(43, 54, 69);
    public static readonly Color Text = new(238, 243, 248);
    public static readonly Color Muted = new(150, 166, 184);
    public static readonly Color Lime = new(187, 255, 123);
    public static readonly Color Orange = new(255, 179, 102);
    public static readonly Color Cyan = new(86, 213, 230);
    public static readonly Color Danger = new(255, 127, 130);
    public static readonly Color GreenSurface = new(31, 48, 39);

    public static string Font(float points = 11, bool bold = false) => ContentContainer.SystemFontName("Segoe UI", bold ? 600 : 400, points);
}
