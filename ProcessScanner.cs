using System.Diagnostics;
using System.Text;

namespace ProjectOpti;

public enum ProcCategory { Other, Background, Browser, Overlay }

public record ProcInfo(string Name, double CpuPercent, double RamMb, int Count, ProcCategory Category);

public record ProcessScanResult(List<ProcInfo> All, bool RustRunning, double ClosableCpu, double ClosableRamMb, double GainPercent)
{
    public string ToDisplayString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Processes (sampled ~1.5 s):");
        sb.AppendLine($"  Rust running: {(RustRunning ? "yes" : "no")}");
        foreach (var p in All.OrderByDescending(x => x.CpuPercent).Take(8))
        {
            string tag = p.Category == ProcCategory.Background ? "[bg]" : p.Category == ProcCategory.Browser ? "[browser]" : p.Category == ProcCategory.Overlay ? "[overlay]" : "";
            sb.AppendLine($"  {p.Name,-22} {p.CpuPercent,5:0.0}% CPU {p.RamMb,7:0} MB  x{p.Count} {tag}");
        }
        sb.AppendLine($"  Closable apps (bg + browsers): {ClosableCpu:0.0}% CPU, {ClosableRamMb:0} MB RAM");
        sb.AppendLine($"  Estimated FPS cost of those: ~{GainPercent:0.#}%");
        return sb.ToString().TrimEnd();
    }
}

public static class ProcessScanner
{
    // Apps that are normally safe to close before gaming. Discord/Steam are NOT in here on purpose.
    public static readonly HashSet<string> BackgroundApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "OneDrive", "ms-teams", "Teams", "Spotify", "Skype", "SkypeApp", "Dropbox", "GoogleDriveFS",
        "EpicGamesLauncher", "Battle.net", "Origin", "EADesktop", "wallpaper32", "wallpaper64",
        "YourPhone", "PhoneExperienceHost"
    };

    // Overlays / monitoring tools that hook into games. Discord is NOT here (reported separately).
    public static readonly HashSet<string> Overlays = new(StringComparer.OrdinalIgnoreCase)
    {
        "MSIAfterburner", "RTSS", "RTSSHooksLoader64", "NVIDIA Overlay", "NVIDIA Share",
        "NVIDIA GeForce Experience", "Overwolf", "GameBar", "GameBarFTServer", "XboxGameBarWidgets",
        "GameBarPresenceWriter", "RadeonSoftware"
    };

    public static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave"
    };

    private record Snap(string Name, TimeSpan Cpu, long Ram);

    public static ProcessScanResult Scan(SystemSpecs specs)
    {
        Logger.Log("Scanning running processes...");
        var before = Snapshot();
        var sw = Stopwatch.StartNew();
        Thread.Sleep(1500);
        var after = Snapshot();
        sw.Stop();

        double wallMs = Math.Max(1, sw.ElapsedMilliseconds);
        int cores = Math.Max(1, Environment.ProcessorCount);

        var groups = new Dictionary<string, (double Cpu, double Ram, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, a) in after)
        {
            if (a.Name == "Idle") continue;
            double cpu = 0;
            if (before.TryGetValue(pid, out var b))
                cpu = (a.Cpu - b.Cpu).TotalMilliseconds / (wallMs * cores) * 100.0;

            groups.TryGetValue(a.Name, out var g);
            groups[a.Name] = (g.Cpu + Math.Max(0, cpu), g.Ram + a.Ram / 1024.0 / 1024.0, g.Count + 1);
        }

        var all = groups.Select(kv => new ProcInfo(kv.Key, kv.Value.Cpu, kv.Value.Ram, kv.Value.Count,
            BackgroundApps.Contains(kv.Key) ? ProcCategory.Background :
            Browsers.Contains(kv.Key) ? ProcCategory.Browser :
            Overlays.Contains(kv.Key) ? ProcCategory.Overlay : ProcCategory.Other)).ToList();

        foreach (var p in all.Where(p => p.Category == ProcCategory.Background || p.Category == ProcCategory.Browser))
            Logger.Log($"  closable: {p.Name} x{p.Count}, {p.CpuPercent:0.0}% CPU, {p.RamMb:0} MB");

        bool rust = all.Any(p => p.Name.Equals("RustClient", StringComparison.OrdinalIgnoreCase));
        double closableCpu = all.Where(p => p.Category == ProcCategory.Background || p.Category == ProcCategory.Browser).Sum(p => p.CpuPercent);
        double closableRam = all.Where(p => p.Category == ProcCategory.Background || p.Category == ProcCategory.Browser).Sum(p => p.RamMb);

        // GUESS: 1% CPU used by background apps ~ 0.5% FPS, 1 GB RAM ~ 0.5% FPS, capped at 10%.
        double gain = Math.Min(10, closableCpu * 0.5 + closableRam / 1024.0 * 0.5);
        Logger.Log($"  total closable: {closableCpu:0.0}% CPU, {closableRam:0} MB -> est. FPS cost {gain:0.#}%");
        Logger.Log($"  Rust running: {rust}");

        return new ProcessScanResult(all, rust, closableCpu, closableRam, gain);
    }

    private static Dictionary<int, Snap> Snapshot()
    {
        var map = new Dictionary<int, Snap>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                TimeSpan cpu = TimeSpan.Zero;
                long ram = 0;
                try { cpu = p.TotalProcessorTime; } catch { /* protected process */ }
                try { ram = p.WorkingSet64; } catch { }
                map[p.Id] = new Snap(p.ProcessName, cpu, ram);
            }
            catch { /* process exited */ }
            finally { p.Dispose(); }
        }
        return map;
    }
}
