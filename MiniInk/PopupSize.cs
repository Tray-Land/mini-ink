namespace MiniInk;

// How big the tray popup opens, in logical pixels. Dragging an edge of the popup sets it, and so does Settings.
public static class PopupSize
{
    public const int DefaultWidth = 440;
    public const int DefaultHeight = 620;
    // Small enough to sit beside the taskbar, but wide and tall enough for the header, the pen tools, and Settings.
    public const int MinWidth = 360;
    public const int MinHeight = 400;
    public const int MaxWidth = 2000;
    public const int MaxHeight = 2000;

    public static int ClampWidth(int width) => Math.Clamp(width, MinWidth, MaxWidth);
    public static int ClampHeight(int height) => Math.Clamp(height, MinHeight, MaxHeight);

    // Settings hold the size as a number; anything unknown falls back to the default.
    public static int Width(object? stored) => stored is int width ? ClampWidth(width) : DefaultWidth;
    public static int Height(object? stored) => stored is int height ? ClampHeight(height) : DefaultHeight;

    // A window's size in pixels back to logical pixels, at the DPI of the display it is on.
    public static int FromPixels(int pixels, uint dpi) => dpi == 0 ? pixels : (int)Math.Round(pixels * 96.0 / dpi);
}
