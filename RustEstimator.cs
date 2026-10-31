using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ProjectOpti;

public record RustResult(
    double CurrentFps, double AfterWindowsFps, double AfterAllFps,
    int? Quality, double? LowerQualityFps, List<Finding> Findings, SkinningResult Skinning)
{
    public string ToDisplayString()
    {
        var nl = Environment.NewLine;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Rust @ 1080p (ESTIMATE, +/-15%, normal branch)");
        sb.AppendLine("--------------------------------");
        sb.AppendLine($"Current:                 ~{CurrentFps:0} FPS");
        sb.AppendLine($"After auto tweaks:       ~{AfterWindowsFps:0} FPS ({Pct(CurrentFps, AfterWindowsFps)})");
        sb.AppendLine($"After your own fixes too:~{AfterAllFps:0} FPS ({Pct(CurrentFps, AfterAllFps)})");
        sb.AppendLine();
        sb.AppendLine(Quality.HasValue
            ? $"Rust graphics.quality: {Quality} (read from client.cfg)"
            : "Rust graphics.quality: not found, assumed 3 (medium)");
        if (LowerQualityFps.HasValue)
            sb.AppendLine($"With graphics.quality 2:  ~{LowerQualityFps:0} FPS (on top of current)");
        sb.AppendLine();
        sb.AppendLine("Normal (CPU skinning) vs GPU skinning test:");
        sb.AppendLine($"  Steam branch now:   {Skinning.Branch}");
        sb.AppendLine($"  Bottleneck:         {Skinning.Bottleneck}");
        sb.AppendLine($"  Normal:             ~{Skinning.NormalFps:0} FPS");
        sb.AppendLine($"  GPU skinning test:  ~{Skinning.GpuSkinFps:0} FPS ({Pct(Skinning.NormalFps, Skinning.GpuSkinFps)})");
        sb.AppendLine($"  Verdict: {Skinning.Verdict}");
        sb.AppendLine("  Switch: Steam > Rust > Properties > Betas > 'GPU Skinning test'");
        sb.AppendLine();
        sb.AppendLine("Things to fix:");
        var bad = Findings.Where(f => !f.Ok).ToList();
        if (bad.Count == 0) sb.AppendLine("  nothing found");
        foreach (var f in bad)
            sb.AppendLine($"  [{(f.Hardware ? "YOU " : "AUTO")}] {f.Name}: {f.Current} -> {f.Fix} (~+{f.GainPercent}%)");
        return sb.ToString().TrimEnd();
    }

    private static string Pct(double from, double to) => from <= 0 ? "n/a" : $"+{(to / from - 1) * 100:0.#}%";
}

public record SkinningResult(string Branch, double NormalFps, double GpuSkinFps, string Bottleneck, string Verdict);

public static class RustEstimator
{
    // GUESSES, not measured: share of CPU frame time spent on skinning (moved to GPU on the test branch)
    // and the extra GPU frame time it costs. Tune these when you have real data.
    private const double SkinCpuShare = 0.12;
    private const double SkinGpuCost = 0.05;

    // Relative score 0-100. Order matters: specific names first.
    private static readonly (string Key, int Score)[] GpuScores =
    {
        ("RTX 5090",100),("RTX 4090",98),("RTX 5080",92),("RTX 4080",90),("RX 7900 XTX",88),("RX 7900 XT",82),
        ("RTX 5070 Ti",84),("RTX 5070",76),("RTX 4070 Ti",78),("RTX 3090",78),("RTX 3080",74),("RX 7800",72),
        ("RTX 4070",70),("RX 6900",70),("RX 6800",68),("RTX 3070",62),("RX 6700",58),("RTX 4060 Ti",58),
        ("RTX 3060 Ti",56),("RTX 5060",56),("RTX 4060",52),("RTX 2080",52),("RX 7600",50),("RTX 3060",46),
        ("RTX 2070",46),("Arc A770",48),("Arc A750",44),("RX 6600",44),("GTX 1080 Ti",42),("RTX 2060",40),
        ("GTX 1660",36),("GTX 1080",38),("GTX 1070",36),("GTX 1060",28),("RX 580",28),("GTX 1650",26),
        ("RTX 3050",32),("GTX 1050 Ti",18),("GTX 1050",14),
    };

    private static readonly (string Key, int Score)[] CpuScores =
    {
        ("Ryzen 7 9800X3D",100),("Ryzen 7 7800X3D",95),("Ryzen 9 7950X",90),("Ryzen 7 5800X3D",85),
        ("Ryzen 7 7700",85),("Ryzen 5 7600",82),("Ryzen 9 5950X",76),("Ryzen 9 5900X",74),
        ("Ryzen 7 5800X",72),("Ryzen 7 5700X",68),("Ryzen 5 5600X",66),("Ryzen 5 5600",64),
        ("Ryzen 5 3600",52),("Ryzen 5 2600",40),("Ryzen 7 3700X",56),("Ryzen 7 2700",44),
        ("i9-14900",92),("i9-13900",90),("i7-14700",88),("i7-13700",85),("i5-14600",85),("i5-13600",83),
        ("i9-12900",82),("i7-12700",76),("i5-12600",72),("i5-12400",65),("i5-11400",58),
        ("i9-10900",62),("i7-10700",58),("i5-10400",52),("i7-9700",52),("i5-9400",45),("i7-8700",50),("i7-7700",40),
    };

    // Rough multiplier relative to graphics.quality 3 (medium)
    private static readonly double[] QualityFactor = { 1.35, 1.2, 1.1, 1.0, 0.85, 0.7 };

    public static RustResult Estimate(SystemSpecs s, List<Finding> findings, int? quality)
    {
        Logger.Log("Estimating GPU score...");
        int gpuScore = Lookup(GpuScores, s.Gpu, out bool gpuFound);
        if (!gpuFound)
        {
            gpuScore = (int)Math.Min(60, s.VramGb * 6);
            Logger.Log($"  GPU not in table, fallback score from VRAM: {gpuScore}");
        }
        else Logger.Log($"  GPU matched, score {gpuScore}");

        Logger.Log("Estimating CPU score...");
        int cpuScore = Lookup(CpuScores, s.Cpu, out bool cpuFound);
        if (!cpuFound)
        {
            cpuScore = (int)Math.Min(100, s.MaxClockGhz * 12 + s.Cores * 2);
            Logger.Log($"  CPU not in table, fallback score from clock/cores: {cpuScore}");
        }
        else Logger.Log($"  CPU matched, score {cpuScore}");

        // Rust is mostly CPU/RAM bound, so the weaker part decides.
        double gpuFps = gpuScore * 1.6;
        double cpuFps = cpuScore * 1.5;
        Logger.Log($"  GPU limit ~{gpuFps:0} FPS, CPU limit ~{cpuFps:0} FPS, bottleneck: {(cpuFps <= gpuFps ? "CPU" : "GPU")}");

        int q = Math.Clamp(quality ?? 3, 0, 5);
        cpuFps *= QualityFactor[q];
        gpuFps *= QualityFactor[q];
        double raw = Math.Min(gpuFps, cpuFps);

        // GPU skinning comparison: CPU frame time shrinks, GPU frame time grows.
        Logger.Log("Comparing normal (CPU skinning) vs GPU skinning test...");
        string branch = RustConfig.ReadSteamBranch();
        double skinRaw = Math.Min(cpuFps / (1 - SkinCpuShare), gpuFps / (1 + SkinGpuCost));
        double ratio = skinRaw / raw;
        double diff = (ratio - 1) * 100;
        string bottleneck = cpuFps <= gpuFps ? "CPU" : "GPU";
        Logger.Log($"  CPU limit {cpuFps:0}, GPU limit {gpuFps:0} -> skinning ratio {ratio:0.000} ({diff:+0.0;-0.0}%)");

        string verdict;
        if (diff >= 3)
            verdict = $"GPU skinning test should be better (~{diff:0}%). Your CPU is the limit and your GPU has headroom.";
        else if (diff <= -3)
            verdict = "Stay on normal. Your GPU is the limit, extra skinning work would hurt.";
        else
            verdict = "No meaningful difference expected. Stay on normal, or try the test branch if you get drops while holding a weapon.";
        Logger.Log("  Verdict: " + verdict);
        var skinning = new SkinningResult(branch, raw / (1 + (findings.Where(f => !f.Ok).Sum(f => f.GainPercent)) / 100.0),
            0, bottleneck, verdict);

        double win = findings.Where(f => !f.Ok && !f.Hardware).Sum(f => f.GainPercent);
        double hw = findings.Where(f => !f.Ok && f.Hardware).Sum(f => f.GainPercent);
        Logger.Log($"  Windows tweak potential {win}%, hardware potential {hw}%");

        double current = raw / (1 + (win + hw) / 100.0);
        double afterWin = raw / (1 + hw / 100.0);
        double afterAll = raw;

        double? lower = q > 2 ? current * QualityFactor[2] / QualityFactor[q] : null;

        Logger.Log($"Result: current ~{current:0}, after Windows ~{afterWin:0}, after all ~{afterAll:0}");
        skinning = skinning with { NormalFps = current, GpuSkinFps = current * ratio };
        return new RustResult(current, afterWin, afterAll, quality, lower, findings, skinning);
    }

    private static int Lookup((string Key, int Score)[] table, string name, out bool found)
    {
        foreach (var (key, score) in table)
        {
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) { found = true; return score; }
        }
        found = false;
        return 0;
    }
}

public static class RustConfig
{
    public static int? ReadGraphicsQuality()
    {
        Logger.Log("Looking for Rust client.cfg...");
        foreach (var lib in SteamLibraries())
        {
            string cfg = Path.Combine(lib, "steamapps", "common", "Rust", "cfg", "client.cfg");
            Logger.Log("  checking " + cfg);
            if (!File.Exists(cfg)) continue;

            try
            {
                string text = File.ReadAllText(cfg);
                var m = Regex.Match(text, @"^\s*graphics\.quality\s+""?(\d+)""?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    int q = int.Parse(m.Groups[1].Value);
                    Logger.Log($"  graphics.quality = {q}");
                    return q;
                }
                Logger.Log("  graphics.quality not present in file");
            }
            catch (Exception ex)
            {
                Logger.Log("  could not read client.cfg: " + ex.Message);
            }
        }

        Logger.Log("Rust client.cfg not found.");
        return null;
    }

    public static string? FindClientCfg()
    {
        foreach (var lib in SteamLibraries())
        {
            string cfg = Path.Combine(lib, "steamapps", "common", "Rust", "cfg", "client.cfg");
            if (File.Exists(cfg)) return cfg;
        }
        return null;
    }

    public static string? FindRustInstallDir()
    {
        foreach (var lib in SteamLibraries())
        {
            string dir = Path.Combine(lib, "steamapps", "common", "Rust");
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    // Rust settings that cost FPS when on. Key names are from memory: missing keys are simply skipped.
    public static readonly string[] HeavyKeys =
    {
        "graphics.vsync", "effects.motionblur", "effects.ao", "effects.sunshafts",
        "effects.lensdirt", "effects.vignet", "effects.bloom"
    };

    public static bool IsOn(string v) =>
        v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";

    public static Dictionary<string, string>? ReadAll()
    {
        string? cfg = FindClientCfg();
        if (cfg == null) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(cfg))
        {
            var m = Regex.Match(line, @"^\s*([A-Za-z0-9_.]+)\s+""?([^""]*)""?\s*$");
            if (m.Success) d[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }
        return d;
    }

    // Rust = Steam app 252490. The selected beta branch is stored as "BetaKey" in its appmanifest.
    public static string ReadSteamBranch()
    {
        Logger.Log("Looking for Rust Steam branch (appmanifest_252490.acf)...");
        foreach (var lib in SteamLibraries())
        {
            string acf = Path.Combine(lib, "steamapps", "appmanifest_252490.acf");
            if (!File.Exists(acf)) continue;
            try
            {
                var m = Regex.Match(File.ReadAllText(acf), @"""BetaKey""\s+""([^""]*)""", RegexOptions.IgnoreCase);
                string branch = m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "public (normal)";
                Logger.Log("  branch: " + branch);
                return branch;
            }
            catch (Exception ex) { Logger.Log("  could not read manifest: " + ex.Message); }
        }
        Logger.Log("  Rust manifest not found, branch unknown.");
        return "unknown";
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var libs = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
            if (!string.IsNullOrEmpty(steam))
            {
                libs.Add(steam);
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), @"""path""\s+""([^""]+)"""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Steam library lookup failed: " + ex.Message);
        }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
