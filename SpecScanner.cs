using System.Management;
using Microsoft.Win32;

namespace ProjectOpti;

public record SystemSpecs(
    string Cpu, int Cores, int Threads, double MaxClockGhz,
    string Gpu, long VramBytes,
    double RamGb, int RamSticks, int RamRatedMhz, int RamCurrentMhz,
    string Os, string Disks,
    string GpuDriverVersion, string GpuVendor, List<string> AllGpus)
{
    public double VramGb => VramBytes / 1024.0 / 1024.0 / 1024.0;

    public string ToDisplayString() =>
        $"CPU:      {Cpu}{Environment.NewLine}" +
        $"Cores:    {Cores} cores / {Threads} threads{Environment.NewLine}" +
        $"Max clock:{MaxClockGhz:0.00} GHz{Environment.NewLine}" +
        $"GPU:      {Gpu}{Environment.NewLine}" +
        $"VRAM:     {VramGb:0.#} GB{Environment.NewLine}" +
        $"Driver:   {GpuVendor} {GpuDriverVersion}{Environment.NewLine}" +
        $"RAM:      {RamGb:0.#} GB in {RamSticks} stick(s){Environment.NewLine}" +
        $"RAM speed:{(RamCurrentMhz > 0 ? RamCurrentMhz : RamRatedMhz)} MHz (rated {RamRatedMhz} MHz){Environment.NewLine}" +
        $"OS:       {Os}{Environment.NewLine}" +
        $"Disks:    {Disks}";
}

public static class SpecScanner
{
    private static IEnumerable<ManagementBaseObject> Query(string wmiClass)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT * FROM {wmiClass}");
        foreach (var o in searcher.Get())
            yield return o;
    }

    public static SystemSpecs Scan()
    {
        // CPU
        Logger.Log("Reading CPU (WMI Win32_Processor)...");
        string cpu = "Unknown"; int cores = 0, threads = 0; double ghz = 0;
        foreach (var o in Query("Win32_Processor"))
        {
            cpu = (o["Name"]?.ToString() ?? "Unknown").Trim();
            cores = Convert.ToInt32(o["NumberOfCores"]);
            threads = Convert.ToInt32(o["NumberOfLogicalProcessors"]);
            ghz = Convert.ToDouble(o["MaxClockSpeed"]) / 1000.0;
            break;
        }
        Logger.Log($"CPU: {cpu} ({cores}C/{threads}T, {ghz:0.00} GHz)");

        // GPU
        Logger.Log("Reading GPU (WMI Win32_VideoController)...");
        string gpu = "Unknown"; long adapterRam = 0; string gpuDriver = "";
        var gpus = Query("Win32_VideoController")
            .Select(o => (Name: o["Name"]?.ToString() ?? "Unknown", Ram: Convert.ToInt64(o["AdapterRAM"]),
                          Drv: o["DriverVersion"]?.ToString() ?? ""))
            .ToList();
        foreach (var g in gpus) Logger.Log($"  found adapter: {g.Name}");

        var pick = gpus.FirstOrDefault(g =>
            g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase));
        if (pick.Name == null && gpus.Count > 0) pick = gpus[0];
        if (pick.Name != null) { gpu = pick.Name; adapterRam = pick.Ram; gpuDriver = pick.Drv; }
        string vendor = gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" :
                        gpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpu.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD" :
                        gpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Unknown";
        var allGpus = gpus.Select(g => g.Name).ToList();
        Logger.Log($"GPU vendor: {vendor}, driver version {gpuDriver}");

        long vram = ReadVramFromRegistry(gpu, adapterRam);
        Logger.Log($"GPU: {gpu}, VRAM {vram / 1024.0 / 1024.0 / 1024.0:0.#} GB");

        // RAM
        Logger.Log("Reading RAM (WMI Win32_PhysicalMemory)...");
        long totalBytes = 0; int sticks = 0, rated = 0, current = 0;
        foreach (var o in Query("Win32_PhysicalMemory"))
        {
            sticks++;
            totalBytes += Convert.ToInt64(o["Capacity"]);
            rated = Math.Max(rated, Convert.ToInt32(o["Speed"]));
            current = Math.Max(current, Convert.ToInt32(o["ConfiguredClockSpeed"]));
        }
        double ramGb = totalBytes / 1024.0 / 1024.0 / 1024.0;
        Logger.Log($"RAM: {ramGb:0.#} GB, {sticks} stick(s), rated {rated} MHz, running {current} MHz");

        // OS
        Logger.Log("Reading OS...");
        string os = "Unknown";
        foreach (var o in Query("Win32_OperatingSystem"))
        {
            os = $"{o["Caption"]} ({o["Version"]})";
            break;
        }

        // Disks
        Logger.Log("Reading disks...");
        var disks = Query("Win32_DiskDrive").Select(o => o["Model"]?.ToString() ?? "?").ToList();
        string diskText = disks.Count > 0 ? string.Join("; ", disks) : "Unknown";

        return new SystemSpecs(cpu, cores, threads, ghz, gpu, vram, ramGb, sticks, rated, current, os, diskText,
            gpuDriver, vendor, allGpus);
    }

    // WMI AdapterRAM is 32-bit (caps at ~4 GB), so read the real value from the registry.
    private static long ReadVramFromRegistry(string gpuName, long fallback)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11cd-b6c9-00a0c9ef2fa3}");
            if (cls == null) return fallback;

            foreach (var name in cls.GetSubKeyNames())
            {
                try
                {
                    using var key = cls.OpenSubKey(name);
                    if (key == null) continue;
                    if (key.GetValue("DriverDesc") as string != gpuName) continue;

                    var v = key.GetValue("HardwareInformation.qwMemorySize");
                    if (v is long l && l > 0) return l;
                    if (v is byte[] b && b.Length >= 8) return BitConverter.ToInt64(b, 0);
                }
                catch { /* skip unreadable subkeys */ }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("VRAM registry read failed: " + ex.Message);
        }

        Logger.Log("Using WMI AdapterRAM for VRAM (may be capped at 4 GB).");
        return fallback;
    }
}
