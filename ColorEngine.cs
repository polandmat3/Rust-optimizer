using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace ProjectOpti;

public record ColorProfile(double Brightness, double Contrast, double Gamma, double Hue, double Saturation, double Temperature)
{
    public static readonly ColorProfile Default = new(0, 0, 1.0, 0, 100, 0);
}

/// <summary>
/// Screen color control without a driver:
///  - Windows Magnification API: a 5x5 color matrix for the whole screen (hue, saturation, contrast, brightness, temperature)
///  - SetDeviceGammaRamp: the gamma curve
/// Everything is undone when the app closes.
/// </summary>
public static class ColorEngine
{
    private static bool _magReady, _rampSaved, _rampChanged;
    private static ushort[]? _originalRamp;

    public static string Status { get; private set; } = "Not applied yet";

    private static string SettingsFile => Path.Combine(AppPaths.Root, "colors.txt");

    // ================= apply / reset =================

    public static void Apply(ColorProfile p)
    {
        string filter;
        try
        {
            if (!_magReady)
            {
                _magReady = MagInitialize();
                if (!_magReady) Logger.Log("Color: Windows Magnification API could not start.");
            }

            if (_magReady)
            {
                var effect = new MAGCOLOREFFECT { transform = BuildMatrix(p) };
                filter = MagSetFullscreenColorEffect(ref effect) ? "active" : "rejected by Windows";
            }
            else filter = "unavailable";
        }
        catch (Exception ex)
        {
            filter = "error";
            Logger.Log("Color: matrix filter error: " + ex.Message);
        }

        string ramp;
        try { ramp = ApplyGamma(p.Gamma); }
        catch (Exception ex)
        {
            ramp = "error";
            Logger.Log("Color: gamma error: " + ex.Message);
        }

        Status = $"Matrix filter: {filter}   |   Gamma ramp: {ramp}";
    }

    /// <summary>Back to normal colors. Called when the app exits.</summary>
    public static void Reset()
    {
        try
        {
            if (_magReady)
            {
                var identity = new MAGCOLOREFFECT { transform = BuildMatrix(ColorProfile.Default) };
                MagSetFullscreenColorEffect(ref identity);
                MagUninitialize();
                _magReady = false;
            }
            if (_rampChanged) ApplyGamma(1.0);
        }
        catch { /* nothing more we can do on exit */ }
    }

    // ================= matrix =================
    // Row-vector convention (like the Windows docs): [R G B A 1] * M, last row = offsets.

    public static float[] BuildMatrix(ColorProfile p)
    {
        // saturation
        double s = p.Saturation / 100.0;
        double[] lum = { 0.2126, 0.7152, 0.0722 };
        var sat = Identity();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                sat[i, j] = (1 - s) * lum[i] + (i == j ? s : 0);

        // hue rotation (column-vector form, transposed into row-vector form)
        double rad = p.Hue * Math.PI / 180.0, cos = Math.Cos(rad), sin = Math.Sin(rad);
        double[,] h =
        {
            { 0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928 },
            { 0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283 },
            { 0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072 }
        };
        var hue = Identity();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                hue[i, j] = h[j, i];

        // temperature: warm = more red / less blue
        double t = p.Temperature / 100.0;
        var temp = Identity();
        temp[0, 0] = 1 + 0.3 * t;
        temp[2, 2] = 1 - 0.3 * t;

        // contrast (scale around mid-gray) + brightness (offset)
        double k = 1 + p.Contrast / 100.0;
        double offset = 0.5 * (1 - k) + p.Brightness / 100.0 * 0.5;
        var cb = Identity();
        cb[0, 0] = cb[1, 1] = cb[2, 2] = k;
        cb[4, 0] = cb[4, 1] = cb[4, 2] = offset;

        var m = Mul(Mul(Mul(sat, hue), temp), cb);

        var result = new float[25];
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                result[i * 5 + j] = (float)m[i, j];
        return result;
    }

    private static double[,] Identity()
    {
        var m = new double[5, 5];
        for (int i = 0; i < 5; i++) m[i, i] = 1;
        return m;
    }

    private static double[,] Mul(double[,] a, double[,] b)
    {
        var r = new double[5, 5];
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
            {
                double sum = 0;
                for (int k = 0; k < 5; k++) sum += a[i, k] * b[k, j];
                r[i, j] = sum;
            }
        return r;
    }

    // ================= gamma ramp =================

    private static string ApplyGamma(double gamma)
    {
        bool identity = Math.Abs(gamma - 1.0) < 0.001;
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (!_rampSaved)
            {
                var cur = new RAMP { Values = new ushort[768] };
                if (GetDeviceGammaRamp(hdc, ref cur)) _originalRamp = cur.Values;
                _rampSaved = true;
            }

            var ramp = new RAMP { Values = new ushort[768] };

            if (identity)
            {
                if (!_rampChanged) return "off";
                if (_originalRamp != null) ramp.Values = _originalRamp;
                else FillGamma(ramp.Values, 1.0);
                bool restored = SetDeviceGammaRamp(hdc, ref ramp);
                if (restored) _rampChanged = false;
                return restored ? "off" : "could not restore";
            }

            FillGamma(ramp.Values, gamma);
            bool ok = SetDeviceGammaRamp(hdc, ref ramp);
            if (ok) _rampChanged = true;
            return ok ? "active" : "rejected by Windows / driver";
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static void FillGamma(ushort[] values, double gamma)
    {
        for (int i = 0; i < 256; i++)
        {
            double v = Math.Pow(i / 255.0, 1.0 / gamma);
            ushort u = (ushort)Math.Clamp(v * 65535.0 + 0.5, 0, 65535);
            values[i] = values[256 + i] = values[512 + i] = u;
        }
    }

    // ================= save / load =================

    public static void Save(ColorProfile p, bool applyOnStartup)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var c = CultureInfo.InvariantCulture;
            File.WriteAllLines(SettingsFile, new[]
            {
                $"startup={(applyOnStartup ? 1 : 0)}",
                $"brightness={p.Brightness.ToString(c)}",
                $"contrast={p.Contrast.ToString(c)}",
                $"gamma={p.Gamma.ToString(c)}",
                $"hue={p.Hue.ToString(c)}",
                $"saturation={p.Saturation.ToString(c)}",
                $"temperature={p.Temperature.ToString(c)}",
            });
        }
        catch { /* not critical */ }
    }

    public static ColorProfile? LoadSaved(out bool applyOnStartup)
    {
        applyOnStartup = false;
        try
        {
            if (!File.Exists(SettingsFile)) return null;

            var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(SettingsFile))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 &&
                    double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    d[parts[0].Trim()] = v;
            }

            applyOnStartup = d.GetValueOrDefault("startup") == 1;
            var def = ColorProfile.Default;
            return new ColorProfile(
                d.GetValueOrDefault("brightness", def.Brightness),
                d.GetValueOrDefault("contrast", def.Contrast),
                d.GetValueOrDefault("gamma", def.Gamma),
                d.GetValueOrDefault("hue", def.Hue),
                d.GetValueOrDefault("saturation", def.Saturation),
                d.GetValueOrDefault("temperature", def.Temperature));
        }
        catch { return null; }
    }

    // ================= native =================

    [StructLayout(LayoutKind.Sequential)]
    private struct MAGCOLOREFFECT
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] transform;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 768)]
        public ushort[] Values;
    }

    [DllImport("Magnification.dll")] private static extern bool MagInitialize();
    [DllImport("Magnification.dll")] private static extern bool MagUninitialize();
    [DllImport("Magnification.dll")] private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT pEffect);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);
    [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);
}
