using SkiaSharp;

namespace Aware.Spatial;

/// <summary>
/// The palette from 05-DESIGN-SYSTEM, in the form the renderer needs.
/// XAML reads the same values from Styles/AwareTokens.xaml; this is the one
/// place they exist as Skia colours.
/// </summary>
internal static class SpatialPalette
{
    public static readonly SKColor CanvasNear = new(0xF8, 0xF7, 0xF3);
    public static readonly SKColor CanvasFar = new(0xDE, 0xDC, 0xD6);
    public static readonly SKColor ModelFront = new(0xF1, 0xF0, 0xEC);
    public static readonly SKColor ModelSide = new(0xD8, 0xD7, 0xD1);
    public static readonly SKColor ModelRear = new(0xC4, 0xC3, 0xBD);
    public static readonly SKColor Floor = new(0xD2, 0xD0, 0xC9);
    public static readonly SKColor Ink = new(0x19, 0x1A, 0x17);
    public static readonly SKColor Muted = new(0x75, 0x77, 0x6F);
    public static readonly SKColor Selection = new(0xB7, 0xC2, 0xB1);
    public static readonly SKColor SelectionDark = new(0x6C, 0x79, 0x67);
    public static readonly SKColor Blueprint = new(0x82, 0x93, 0x9A);

    public static SKColor Lerp(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(a.Red + (b.Red - a.Red) * t),
            (byte)(a.Green + (b.Green - a.Green) * t),
            (byte)(a.Blue + (b.Blue - a.Blue) * t),
            (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
    }

    public static SKColor WithAlpha(SKColor c, float alpha) =>
        c.WithAlpha((byte)Math.Clamp(alpha * 255f, 0f, 255f));
}
