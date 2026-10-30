#if DEBUG
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Later.App;

/// Debug only: PrintWindow works on the x64 and ARM64 runners; a plain screen copy works on x64 only (spike).
internal static partial class ScreenCapture
{
    [LibraryImport("user32.dll")] private static partial int PrintWindow(nint hwnd, nint hdc, uint flags);
    [LibraryImport("user32.dll")] private static partial int GetWindowRect(nint hwnd, out Rect rect);
#pragma warning disable CS0649 // filled in by GetWindowRect
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
#pragma warning restore CS0649
    const uint PwRenderFullContent = 0x2;

    public static void Save(nint hwnd, string? printWindowPath, string? screenPath)
    {
        _ = GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (printWindowPath is not null)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { _ = PrintWindow(hwnd, hdc, PwRenderFullContent); } finally { g.ReleaseHdc(hdc); }
            }
            bmp.Save(printWindowPath, ImageFormat.Png);
        }
        if (screenPath is not null)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
            bmp.Save(screenPath, ImageFormat.Png);
        }
    }
}
#endif
