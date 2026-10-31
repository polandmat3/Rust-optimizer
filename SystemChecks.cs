using System.IO;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;

namespace ProjectOpti;

public static class Admin
{
    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}

/// <summary>
/// Extra checks beyond the basics in OptimizationScanner.
/// "Hardware = true" in a Finding means YOU have to act (no automatic fix).
/// </summary>
public static class SystemChecks
{
    private static readonly HashSet<string> UpdateProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "TiWorker", "TrustedInstaller", "SearchIndexer", "SearchProtocolHost", "MsMpEng", "MoUsoCoreWorker", "wuauclt"
    };

    public static List<Finding> Run(SystemSpecs specs, ProcessScanResult procs, DriverInfo driver)
    {
        var list = new List<Finding>();
        Try("GPU driver", () => CheckDriver(list, driver));
        Try("Overlays", () => CheckOverlays(list, procs));
        Try("Startup apps", () => CheckStartup(list));
        Try("Transparency", () => CheckTransparency(list));
        Try("Update/indexing load", () => CheckUpdateLoad(list, procs));
        Try("Memory Integrity", CheckVbs(list));
        Try("Hybrid GPU", () => CheckHybridGpu(list, specs));
        Try("Power source", () => CheckPowerSource(list));
        Try("RAM size", () => CheckRamSize(list, specs));
        Try("Rust drive", () => CheckRustDrive(list));
        Try("Defender exclusion", () => CheckDefender(list));
        Try("Rust config", () => CheckRustConfig(list));
        Try("Temperatures", () => CheckThermals(list, procs));
        return list;
    }

    private static void Try(string name, Action a)
    {
        Logger.Log($"Check: {name}...");
        try { a(); }
        catch (Exception ex) { Logger.Log($"  check '{name}' failed: {ex.Message}"); }
    }

    private static Action CheckVbs(List<Finding> list) => () =>
    {
        int? v = ReadDword(Registry.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        bool on = v == 1;
        list.Add(new("Memory Integrity (VBS)", on ? "On" : "Off", true, 0,
            on ? "(manual, security trade-off) can cost a few % in games: Windows Security > Device security > Core isolation. NOT changed automatically."
               : "", false));
    };

    // ---- individual checks ----

    private static void CheckDriver(List<Finding> list, DriverInfo d)
    {
        if (d.UpToDate == false)
            list.Add(new("GPU driver", $"{d.Installed} (latest {d.Latest})", false, 2,
                $"Install the newest {d.Vendor} driver: {d.Url} (Tweaks can open the page)", true));
        else
            list.Add(new("GPU driver", d.UpToDate == true ? $"{d.Installed} (up to date)" : $"{d.Installed} (could not compare)",
                true, 0, "", true));
    }

    private static void CheckOverlays(List<Finding> list, ProcessScanResult procs)
    {
        var ov = procs.All.Where(p => p.Category == ProcCategory.Overlay).ToList();
        list.Add(new("Overlays / monitoring tools",
            ov.Count == 0 ? "none running" : string.Join(", ", ov.Select(p => p.Name)),
            ov.Count == 0, Math.Min(4, ov.Count * 1.0),
            "Close them (Tweaks: close overlay apps)", false));

        if (procs.All.Any(p => p.Name.Equals("Discord", StringComparison.OrdinalIgnoreCase)))
            list.Add(new("Discord overlay", "Discord running", false, 0,
                "(manual) Discord > Settings > Game Overlay > off. Discord is never closed automatically.", false));
    }

    private static void CheckStartup(List<Finding> list)
    {
        var items = Tweaks.FindStartupBloat(includeMachine: true);
        foreach (var i in items) Logger.Log($"  startup: {i.Name} ({(i.Machine ? "all users" : "current user")})");
        list.Add(new("Startup apps", items.Count == 0 ? "none of the known heavy ones" : $"{items.Count} known app(s)",
            items.Count == 0, Math.Min(3, items.Count * 0.3),
            "Disable startup entries (Tweaks), or Task Manager > Startup", false));
    }

    private static void CheckTransparency(List<Finding> list)
    {
        int? v = ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency");
        bool on = v != 0;
        list.Add(new("Windows transparency effects", on ? "On" : "Off", !on, on ? 0.3 : 0,
            "Turn transparency off (Tweaks)", false));
    }

    private static void CheckUpdateLoad(List<Finding> list, ProcessScanResult procs)
    {
        var busy = procs.All.Where(p => UpdateProcs.Contains(p.Name) && p.CpuPercent >= 3).ToList();
        list.Add(new("Windows Update / indexing / scan load",
            busy.Count == 0 ? "quiet" : string.Join(", ", busy.Select(p => $"{p.Name} {p.CpuPercent:0}%")),
            busy.Count == 0, 0,
            "(manual) let it finish, or schedule updates and scans outside play time", false));
    }

    private static bool IsHybrid(SystemSpecs s) =>
        s.AllGpus.Count(g => !g.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) >= 2 &&
        (s.Gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
         s.Gpu.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
         s.Gpu.Contains("Arc", StringComparison.OrdinalIgnoreCase));

    public static bool HybridGpu(SystemSpecs s) => IsHybrid(s);

    private static void CheckHybridGpu(List<Finding> list, SystemSpecs specs)
    {
        if (!IsHybrid(specs))
        {
            list.Add(new("GPU assignment", "single GPU / not hybrid", true, 0, "", false));
            return;
        }

        string? dir = RustConfig.FindRustInstallDir();
        string exe = dir == null ? "" : Path.Combine(dir, "RustClient.exe");
        bool ok = exe.Length > 0 && Tweaks.GetGpuPreference(exe) == "GpuPreference=2;";
        list.Add(new("GPU assignment (2+ GPUs)", ok ? "Rust set to high-performance GPU" : "Windows decides",
            ok || dir == null, ok || dir == null ? 0 : 5,
            "Force Rust onto the high-performance GPU (Tweaks)", false));
    }

    private static void CheckPowerSource(List<Finding> list)
    {
        bool onBattery = System.Windows.SystemParameters.PowerLineStatus == System.Windows.PowerLineStatus.Offline;
        list.Add(new("Power source", onBattery ? "On battery" : "Plugged in / desktop", !onBattery, onBattery ? 10 : 0,
            "Plug in the charger", true));
    }

    private static void CheckRamSize(List<Finding> list, SystemSpecs specs)
    {
        bool low = specs.RamGb <= 8.5;
        list.Add(new("RAM size", $"{specs.RamGb:0.#} GB", !low, low ? 8 : 0,
            "8 GB or less causes paging in Rust: upgrade to 16 GB+", true));
    }

    private static void CheckRustDrive(List<Finding> list)
    {
        string? dir = RustConfig.FindRustInstallDir();
        if (dir == null)
        {
            Logger.Log("  Rust install folder not found, skipping drive checks.");
            return;
        }

        string root = Path.GetPathRoot(dir)!;
        var di = new DriveInfo(root);
        double freeGb = di.AvailableFreeSpace / 1e9;
        double pct = di.AvailableFreeSpace * 100.0 / di.TotalSize;
        bool spaceOk = pct >= 10 && freeGb >= 30;
        list.Add(new("Rust drive free space", $"{root} {freeGb:0} GB free ({pct:0}%)", spaceOk, spaceOk ? 0 : 1,
            $"Free up space on {root}", true));

        string media = GetMediaType(root[0]);
        Logger.Log($"  Rust drive {root} type: {media}");
        list.Add(new("Rust drive type", $"{root} {media}", media != "HDD", media == "HDD" ? 3 : 0,
            "Move Rust to an SSD (Steam > Settings > Storage)", true));
    }

    private static string GetMediaType(char letter)
    {
        int? diskNumber = null;
        using (var parts = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Partition"))
        {
            foreach (var o in parts.Get())
            {
                var dl = o["DriveLetter"];
                if (dl == null) continue;
                char c = dl is char ch ? ch : (char)Convert.ToUInt16(dl);
                if (char.ToUpperInvariant(c) == char.ToUpperInvariant(letter))
                {
                    diskNumber = Convert.ToInt32(o["DiskNumber"]);
                    break;
                }
            }
        }
        if (diskNumber == null) return "unknown";

        using var disks = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk");
        foreach (var d in disks.Get())
        {
            if (d["DeviceId"]?.ToString() != diskNumber.ToString()) continue;
            int bus = Convert.ToInt32(d["BusType"]);
            int media = Convert.ToInt32(d["MediaType"]);
            if (bus == 17) return "NVMe SSD";
            return media switch { 3 => "HDD", 4 => "SSD", _ => "unknown" };
        }
        return "unknown";
    }

    private static void CheckDefender(List<Finding> list)
    {
        if (!Admin.IsAdmin)
        {
            Logger.Log("  not running as admin: Defender exclusions can't be read.");
            list.Add(new("Defender exclusion for Rust", "unknown (needs admin)", true, 0, "", false));
            return;
        }

        string? dir = RustConfig.FindRustInstallDir();
        if (dir == null) return;
        if (!Tweaks.DefenderAvailable(out bool excluded, dir)) return;

        list.Add(new("Defender exclusion for Rust", excluded ? "added" : "not added", excluded, excluded ? 0 : 1.5,
            "Exclude the Rust folder from real-time scanning (Tweaks, admin). Small security trade-off.", false));
    }

    private static void CheckRustConfig(List<Finding> list)
    {
        var cfg = RustConfig.ReadAll();
        if (cfg == null)
        {
            Logger.Log("  client.cfg not found.");
            return;
        }

        var present = RustConfig.HeavyKeys.Where(k => cfg.ContainsKey(k)).ToList();
        Logger.Log($"  client.cfg: found {present.Count} of {RustConfig.HeavyKeys.Length} known keys");
        var on = present.Where(k => RustConfig.IsOn(cfg[k])).ToList();
        foreach (var k in on) Logger.Log($"  on: {k} = {cfg[k]}");

        list.Add(new("Rust effects / V-Sync",
            on.Count == 0 ? "none of the known heavy ones on" : string.Join(", ", on),
            on.Count == 0, Math.Min(5, on.Count * 0.8),
            "Turn them off (Tweaks edits client.cfg while Rust is closed)", false));
    }

    private static void CheckThermals(List<Finding> list, ProcessScanResult procs)
    {
        var (cpu, gpu) = ThermalReader.Read();
        Logger.Log($"  CPU temp: {(cpu.HasValue ? $"{cpu:0} C" : "n/a")}, GPU temp: {(gpu.HasValue ? $"{gpu:0} C" : "n/a")}");
        if (!cpu.HasValue && !Admin.IsAdmin) Logger.Log("  CPU temperature needs administrator rights.");

        bool hot = cpu >= 85 || gpu >= 83;
        string text = $"CPU {(cpu.HasValue ? $"{cpu:0} C" : "n/a")}, GPU {(gpu.HasValue ? $"{gpu:0} C" : "n/a")}" +
                      (procs.RustRunning ? "" : " (idle reading: start Rust and Calculate again)");
        list.Add(new("Temperatures", text, !hot, hot ? 8 : 0, "Clean dust, improve airflow, repaste", true));
    }

    private static int? ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) is int i ? i : null;
        }
        catch { return null; }
    }
}
