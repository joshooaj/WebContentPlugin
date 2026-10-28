using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using System.Runtime.InteropServices;

namespace WebContent.UxTests.Harness;

public static class Artifacts
{
    public static string RunDirectory { get; private set; }

    public static void Init(string root)
    {
        RunDirectory = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(RunDirectory);
        EnsureDpiAware();
    }

    public static string PathOf(string fileName)
    {
        Directory.CreateDirectory(RunDirectory);
        return Path.Combine(RunDirectory, fileName);
    }

    public static string Window(Window window, string name)
    {
        var file = PathOf(name + ".png");
        using var bmp = CapturePhysicalWindow(window);
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        return file;
    }

    public static string Element(AutomationElement element, string name)
    {
        var file = PathOf(name + ".png");
        Capture.Element(element).ToFile(file);
        return file;
    }

    public static void WriteText(string fileName, string content) => File.WriteAllText(PathOf(fileName), content);

    /// <summary>
    /// Captures the window's PHYSICAL screen pixels (Win32 GetWindowRect), then crops to the union
    /// of the given UIA rectangles plus padding. UIA rects are in the client app's own DPI space;
    /// for DPI-unaware apps (Smart Client) they are smaller than the window's physical pixels, so
    /// they are scaled by the window's physical/UIA ratio before cropping.
    /// </summary>
    public static string WindowCrop(Window window, string name, params System.Drawing.Rectangle[] regions)
    {
        var fullFile = PathOf(name + "-full.png");
        using (var full = CapturePhysicalWindow(window))
            full.Save(fullFile, System.Drawing.Imaging.ImageFormat.Png);
        if (regions.Length == 0) return fullFile;

        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var r in regions)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
        }
        if (left > right || top > bottom) return fullFile;

        using var src = new System.Drawing.Bitmap(fullFile);
        var (phys, uia) = WindowRects(window);
        var sx = (double)phys.Width / Math.Max(1.0, uia.Width);
        var sy = (double)phys.Height / Math.Max(1.0, uia.Height);
        const int pad = 12;
        var x = Math.Max(0, (int)Math.Round((left - uia.X) * sx) - pad);
        var y = Math.Max(0, (int)Math.Round((top - uia.Y) * sy) - pad);
        var w = Math.Min(src.Width - x, (int)Math.Round((right - left) * sx) + pad * 2);
        var h = Math.Min(src.Height - y, (int)Math.Round((bottom - top) * sy) + pad * 2);
        if (w <= 0 || h <= 0) return fullFile;

        using var dst = src.Clone(new System.Drawing.Rectangle(x, y, w, h), src.PixelFormat);
        var file = PathOf(name + ".png");
        dst.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        return file;
    }

    /// <summary>Physical window rect (device pixels) and the UIA-reported rect (possibly DPI-virtualized).</summary>
    private static (System.Drawing.Rectangle Physical, System.Drawing.Rectangle Uia) WindowRects(Window window)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        GetWindowRect(hwnd, out var r);
        var phys = new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var uia = window.BoundingRectangle;
        if (uia.Width <= 0 || phys.Width <= 0) uia = phys;
        return (phys, uia);
    }

    private static System.Drawing.Bitmap CapturePhysicalWindow(Window window)
    {
        EnsureDpiAware();
        var (phys, _) = WindowRects(window);
        var bmp = new System.Drawing.Bitmap(phys.Width, phys.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(phys.X, phys.Y, 0, 0, phys.Size);
        return bmp;
    }

    // The test process must be DPI-aware so GetWindowRect / CopyFromScreen use real device pixels;
    // without it, a scaled display silently re-virtualizes these coordinates.
    private static bool _dpiAware;
    private static void EnsureDpiAware()
    {
        if (_dpiAware) return;
        _dpiAware = true;
        try { SetProcessDPIAware(); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
