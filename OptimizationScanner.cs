using System.Diagnostics;
using Microsoft.Win32;

namespace ProjectOpti;

/// <param name="GainPercent">Rough FPS gain if this gets fixed (estimate, not measured).</param>
public record Finding(string Name, string Current, bool Ok, double GainPercent, string Fix, bool Hardware);

public static class OptimizationScanner
{
    public static List<Finding> Scan(SystemSpecs specs, ProcessScanResult procs, DriverInfo driver)
    {
        var list = new List<Finding>();

        // Power plan
        Logger.Log("Checking power plan (powercfg /getactivescheme)...");
        string plan = RunPowerCfg();
        Logger.Log("  -> " + plan);
        string p = plan.ToLowerInvariant();
        if (p.Contains("a1841308-3541-4fab-bc81-f71556f20b4a"))
            list.Add(new("Power plan", "Power saver", false, 8, "Switch to High performance / Ultimate Performance", false));
        else if (p.Contains("381b4222-f694-41f0-9685-ff5bb260df2e"))
            list.Add(new("Power plan", "Balanced", false, 2, "Switch to High performance (small gain on modern CPUs)", false));
        else
            list.Add(new("Power plan", "High performance / custom", true, 0, "", false));

        // Game Mode (missing value = default = on)
        Logger.Log("Checking Game Mode...");
        int? gameMode = ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        bool gmOn = gameMode != 0;
        Logger.Log($"  -> AutoGameModeEnabled = {(gameMode?.ToString() ?? "not set (default on)")}");
        list.Add(new("Game Mode", gmOn ? "On" : "Off", gmOn, gmOn ? 0 : 1, "Turn Game Mode on in Windows settings", false));

        // Background game capture / DVR
        Logger.Log("Checking background game capture (Game DVR)...");
        int? capture = ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
        bool captureOn = capture == 1;
        Logger.Log($"  -> AppCaptureEnabled = {(capture?.ToString() ?? "not set")}");
        list.Add(new("Background capture", captureOn ? "On" : "Off", !captureOn, captureOn ? 2 : 0,
            "Disable background recording in Xbox Game Bar / Captures", false));

        // HAGS (informational, gain varies per system so 0)
        Logger.Log("Checking Hardware-Accelerated GPU Scheduling...");
        int? hags = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        Logger.Log($"  -> HwSchMode = {(hags?.ToString() ?? "not set")}");
        list.Add(new("HAGS", hags == 2 ? "On" : "Off / unsupported", true, 0,
            "Effect varies by GPU/driver; test both", false));

        // RAM channels (heuristic: 1 stick = single channel)
        Logger.Log("Checking RAM channel config...");
        bool single = specs.RamSticks == 1;
        Logger.Log($"  -> {specs.RamSticks} stick(s)");
        list.Add(new("RAM channels", single ? "Single stick (single channel)" : $"{specs.RamSticks} sticks", !single,
            single ? 10 : 0, "Use 2 matching sticks for dual channel (hardware change)", true));

        // RAM speed vs rated (XMP/EXPO)
        Logger.Log("Checking RAM speed vs rated speed (XMP/EXPO)...");
        bool slow = specs.RamCurrentMhz > 0 && specs.RamRatedMhz > 0 && specs.RamCurrentMhz < specs.RamRatedMhz * 0.9;
        Logger.Log($"  -> running {specs.RamCurrentMhz} MHz, rated {specs.RamRatedMhz} MHz");
        list.Add(new("RAM speed", $"{specs.RamCurrentMhz} / {specs.RamRatedMhz} MHz", !slow, slow ? 5 : 0,
            "Enable XMP/EXPO in BIOS", true));

        // Background processes
        list.Add(new("Background apps",
            $"{procs.ClosableCpu:0.0}% CPU, {procs.ClosableRamMb:0} MB",
            procs.GainPercent < 0.5, procs.GainPercent,
            "Close background apps (tick it in Tweaks, then Run tweaks)", false));

        // Everything else (drivers, overlays, startup, drives, thermals, Rust config...)
        list.AddRange(SystemChecks.Run(specs, procs, driver));

        foreach (var f in list)
            Logger.Log($"Finding: {f.Name} = {f.Current} | ok={f.Ok} | est. gain {f.GainPercent}%");

        return list;
    }

    private static int? ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) is int i ? i : null;
        }
        catch (Exception ex)
        {
            Logger.Log($"Registry read failed ({path}\\{name}): {ex.Message}");
            return null;
        }
    }

    private static string RunPowerCfg()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return "unknown";
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output.Trim();
        }
        catch (Exception ex)
        {
            Logger.Log("powercfg failed: " + ex.Message);
            return "unknown";
        }
    }
}
