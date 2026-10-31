using System.Runtime.InteropServices;

namespace ProjectOpti;

public record AutoResult(ColorProfile? Profile, string Summary);

/// <summary>
/// "Auto adjust": takes a screenshot of the primary screen (with neutral colors applied),
/// measures brightness, contrast range and color saturation, and picks settings that look better.
/// </summary>
public static class AutoColor
{
    public static AutoResult Analyze()
    {
        var (pixels, w, h) = Capture();
        if (pixels == null)
            return new AutoResult(null, "Could not capture the screen.");

        const int step = 4; // sample every 4th pixel in both directions
        var hist = new long[256];
        long count = 0, satCount = 0;
        double sumY = 0, sumSat = 0;

        for (int y = 0; y < h; y += step)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x += step)
            {
                int i = row + x * 4;
                double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
                double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                hist[(int)Math.Min(255, lum * 255)]++;
                sumY += lum;
                count++;

                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max > 0.05)
                {
                    sumSat += (max - min) / max;
                    satCount++;
                }
            }
        }

        if (count == 0) return new AutoResult(null, "Nothing to analyse.");

        double meanL = sumY / count;
        double meanSat = satCount > 0 ? sumSat / satCount : 0;
        double p2 = Percentile(hist, count, 0.02), p98 = Percentile(hist, count, 0.98);
        double range = p98 - p2;

        string stats = $"brightness avg {meanL * 100:0}%, range {range * 100:0}%, saturation avg {meanSat * 100:0}%";

        if (p98 < 0.06)
            return new AutoResult(null, "The capture was black (exclusive fullscreen games can't be captured). Use borderless/windowed mode. " + stats);
        if (range < 0.05)
            return new AutoResult(null, "The picture looked flat (a single color), nothing to adjust. " + stats);

        // contrast: stretch the used range towards ~90% of the full range
        double k = range < 0.9 ? Math.Clamp(0.9 / range, 1.0, 1.35) : 1.0;
        double contrast = Math.Round((k - 1) * 100, 0);

        // gamma: nudge the average brightness towards ~45%, but only halfway and within safe limits
        double gTarget = Math.Log(Math.Max(meanL, 0.02)) / Math.Log(0.45);
        double gamma = Math.Round(Math.Clamp(1 + (gTarget - 1) * 0.5, 0.9, 1.3), 2);

        // saturation: lift dull pictures, calm oversaturated ones
        double saturation;
        if (meanSat < 0.42) saturation = Math.Clamp(100 * Math.Pow(0.42 / Math.Max(meanSat, 0.05), 0.6), 100, 140);
        else if (meanSat > 0.60) saturation = 95;
        else saturation = 100;
        saturation = Math.Round(saturation, 0);

        var profile = new ColorProfile(0, contrast, gamma, 0, saturation, 0);
        return new AutoResult(profile, $"Measured: {stats}. Chosen: contrast {contrast:0}, gamma {gamma:0.00}, saturation {saturation:0}%.");
    }

    // ---------- screen capture (plain GDI, no extra packages) ----------

    private static (byte[]? Pixels, int Width, int Height) Capture()
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1); // primary screen
        if (w <= 0 || h <= 0) return (null, 0, 0);

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);

        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, 0, 0, 0x00CC0020 | 0x40000000))
                return (null, 0, 0);

            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };

            var buffer = new byte[w * h * 4];
            int lines = GetDIBits(mem, bmp, 0, (uint)h, buffer, ref header, 0);
            return lines == 0 ? (null, 0, 0) : (buffer, w, h);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static double Percentile(long[] hist, long total, double p)
    {
        long target = (long)(total * p), run = 0;
        for (int i = 0; i < 256; i++)
        {
            run += hist[i];
            if (run >= target) return i / 255.0;
        }
        return 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
}
