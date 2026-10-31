using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ProjectOpti;

public record Tweak(string Id, string Name, string Description, bool DefaultOn, Action Run, Func<bool> Needed, bool NeedsAdmin = false);

public record StartupItem(bool Machine, string Name);

public static class Tweaks
{
    private const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string GpuPrefPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>Set by MainWindow after the hardware scan.</summary>
    public static SystemSpecs? CurrentSpecs { get; set; }

    private static readonly string[] StartupBloat =
    {
        "OneDrive", "Spotify", "Teams", "MicrosoftEdgeAutoLaunch", "Skype", "iCUE", "Razer", "Synapse", "LGHUB",
        "Armoury", "SteelSeries", "Logitech Download", "Adobe", "CCXProcess", "AcroTray", "EpicGamesLauncher",
        "Battle.net", "Origin", "EADM", "Dropbox", "GoogleDrive", "Wallpaper", "Zoom", "uTorrent", "BitTorrent",
        "Overwolf", "CyberLink"
    };

    public static readonly List<Tweak> All = new()
    {
        new("power", "Power plan -> High performance",
            "Switches the active plan. Old plan is written to the log so you can switch back.", true,
            SetHighPerformance, NeedsPower),
        new("gamemode", "Turn Windows Game Mode on",
            "Registry: HKCU GameBar AutoGameModeEnabled = 1", true,
            EnableGameMode, NeedsGameMode),
        new("dvr", "Turn background game capture off",
            "Registry: AppCaptureEnabled = 0, GameDVR_Enabled = 0", true,
            DisableCapture, NeedsCapture),
        new("closebg", "Close background apps (OneDrive, Teams, Spotify, Skype, launchers...)",
            "Graceful close first, force close only if needed. Discord and Steam are NOT touched.", true,
            () => CloseProcesses(ProcessScanner.BackgroundApps, allowKill: true),
            () => AnyRunning(ProcessScanner.BackgroundApps)),
        new("closeoverlays", "Close overlay / monitoring apps (Afterburner, RTSS, GeForce overlay, Game Bar...)",
            "Hooks into games and costs FPS. Off by default in case you want your monitoring tools.", false,
            () => CloseProcesses(ProcessScanner.Overlays, allowKill: true),
            () => AnyRunning(ProcessScanner.Overlays)),
        new("closebrowsers", "Close web browsers (Chrome, Edge, Firefox...)",
            "Graceful close only (asks the browser to close its window). Save your work first.", false,
            () => CloseProcesses(ProcessScanner.Browsers, allowKill: false),
            () => AnyRunning(ProcessScanner.Browsers)),
        new("startup", "Disable known heavy startup apps",
            "Marks them disabled in Startup apps (re-enable in Task Manager > Startup). Machine-wide ones need admin.", false,
            DisableStartup, () => FindStartupBloat(Admin.IsAdmin).Count > 0),
        new("transparency", "Turn Windows transparency effects off",
            "Registry: EnableTransparency = 0 (tiny gain)", false,
            DisableTransparency, NeedsTransparency),
        new("gpupref", "Force Rust onto the high-performance GPU",
            "Windows Graphics settings entry for RustClient.exe (only shown on PCs with 2+ GPUs).", true,
            SetGpuPreference, NeedsGpuPref),
        new("rustprio", "Rust process priority -> High",
            "Resets when Rust closes.", true,
            SetRustPriority, NeedsRustPriority),
        new("rustquality", "Rust graphics.quality -> 2 (edits client.cfg)",
            "Rust must be closed. Backup saved as client.cfg.projectopti.bak", false,
            SetRustQuality, NeedsRustQuality),
        new("rusteffects", "Rust: turn off V-Sync and heavy effects (edits client.cfg)",
            "Motion blur, AO, sun shafts, lens dirt, vignette, bloom, V-Sync. Rust must be closed. Key names are best-effort.", false,
            LowerRustEffects, NeedsRustEffects),
        new("defender", "Windows Defender: exclude the Rust folder (admin)",
            "Stops real-time scanning of game files. Small security trade-off, so it's off by default.", false,
            AddDefenderExclusion, NeedsDefender, NeedsAdmin: true),
        new("opendriver", "Open the official GPU driver download page",
            "Opens the NVIDIA/AMD page in your browser. Drivers are never installed silently.", false,
            OpenDriverPage, () => DriverChecker.Last?.UpToDate == false),
    };

    /// <summary>Only the tweaks that would actually change something right now.</summary>
    public static List<Tweak> GetNeeded()
    {
        bool admin = Admin.IsAdmin;
        var list = new List<Tweak>();
        foreach (var t in All)
        {
            if (t.NeedsAdmin && !admin)
            {
                Logger.Log($"Tweak check: {t.Name} -> needs admin, hidden");
                continue;
            }

            bool needed;
            try { needed = t.Needed(); }
            catch (Exception ex)
            {
                Logger.Log($"Could not check '{t.Name}': {ex.Message} (showing it anyway)");
                needed = true;
            }
            Logger.Log($"Tweak check: {t.Name} -> {(needed ? "can be applied" : "already set / not applicable, hidden")}");
            if (needed) list.Add(t);
        }
        return list;
    }

    public static bool AnyNeedAdmin => All.Any(t => t.NeedsAdmin);

    public static void Run(IEnumerable<Tweak> selected)
    {
        foreach (var t in selected)
        {
            Logger.Log($"--- Tweak: {t.Name} ---");
            try
            {
                t.Run();
                Logger.Log("  finished");
            }
            catch (Exception ex)
            {
                Logger.Log("  FAILED: " + ex.Message);
            }
        }
    }

    // ================= tweaks =================

    private static void SetHighPerformance()
    {
        Logger.Log("  previous plan: " + Exec("powercfg", "/getactivescheme") + "  (revert: powercfg /setactive <GUID>)");
        Exec("powercfg", $"/setactive {HighPerfGuid}");
        string now = Exec("powercfg", "/getactivescheme");
        Logger.Log("  active plan now: " + now);
        if (!now.Contains(HighPerfGuid, StringComparison.OrdinalIgnoreCase))
            Logger.Log("  could not switch (High performance plan may be hidden on this PC).");
    }

    private static void EnableGameMode()
    {
        SetDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1);
        SetDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1);
    }

    private static void DisableCapture()
    {
        SetDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0);
        SetDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0);
    }

    private static void DisableTransparency() =>
        SetDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0);

    private static void CloseProcesses(HashSet<string> names, bool allowKill)
    {
        int closed = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!names.Contains(p.ProcessName)) continue;

                Logger.Log($"  closing {p.ProcessName} (PID {p.Id})...");
                bool exited = false;

                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    p.CloseMainWindow();
                    exited = p.WaitForExit(3000);
                }

                if (!exited && allowKill)
                {
                    p.Kill();
                    exited = p.WaitForExit(2000);
                }

                if (exited) closed++;
                else Logger.Log("    still running (left alone)");
            }
            catch (Exception ex)
            {
                Logger.Log($"    could not close: {ex.Message}");
            }
            finally { p.Dispose(); }
        }
        Logger.Log($"  closed {closed} process(es)");
    }

    private static void DisableStartup()
    {
        var items = FindStartupBloat(Admin.IsAdmin);
        foreach (var it in items)
        {
            var root = it.Machine ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.CreateSubKey(ApprovedPath);
            var data = new byte[12];
            data[0] = 3; // 2 = enabled, 3 = disabled (same as Task Manager)
            key.SetValue(it.Name, data, RegistryValueKind.Binary);
            Logger.Log($"  disabled startup entry: {it.Name} ({(it.Machine ? "all users" : "current user")})");
        }
        if (items.Count == 0) Logger.Log("  nothing to disable.");
    }

    private static void SetGpuPreference()
    {
        string? dir = RustConfig.FindRustInstallDir();
        string exe = dir == null ? "" : Path.Combine(dir, "RustClient.exe");
        if (!File.Exists(exe))
        {
            Logger.Log("  RustClient.exe not found.");
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(GpuPrefPath);
        object? old = key.GetValue(exe);
        key.SetValue(exe, "GpuPreference=2;", RegistryValueKind.String);
        Logger.Log($"  {exe}: {old ?? "(not set)"} -> GpuPreference=2; (high performance GPU)");
    }

    private static void SetRustPriority()
    {
        var procs = Process.GetProcessesByName("RustClient");
        if (procs.Length == 0)
        {
            Logger.Log("  Rust is not running. Start Rust first, then run this tweak.");
            return;
        }
        foreach (var p in procs)
        {
            p.PriorityClass = ProcessPriorityClass.High;
            Logger.Log($"  RustClient (PID {p.Id}) priority -> High");
            p.Dispose();
        }
    }

    private static void SetRustQuality()
    {
        if (Process.GetProcessesByName("RustClient").Length > 0)
        {
            Logger.Log("  Rust is running and would overwrite the config on exit. Close Rust and run this again.");
            return;
        }

        string? cfg = RustConfig.FindClientCfg();
        if (cfg == null)
        {
            Logger.Log("  client.cfg not found.");
            return;
        }

        string text = File.ReadAllText(cfg);
        var rx = new Regex(@"^(\s*graphics\.quality\s+)""?\d+""?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!rx.IsMatch(text))
        {
            Logger.Log("  graphics.quality not present in client.cfg, nothing changed.");
            return;
        }

        BackupOnce(cfg);
        File.WriteAllText(cfg, rx.Replace(text, "$1\"2\""));
        Logger.Log($"  graphics.quality set to 2 in {cfg}");
    }

    private static void LowerRustEffects()
    {
        if (Process.GetProcessesByName("RustClient").Length > 0)
        {
            Logger.Log("  Rust is running and would overwrite the config on exit. Close Rust and run this again.");
            return;
        }

        string? cfg = RustConfig.FindClientCfg();
        if (cfg == null)
        {
            Logger.Log("  client.cfg not found.");
            return;
        }

        string text = File.ReadAllText(cfg);
        int changed = 0;
        foreach (var key in RustConfig.HeavyKeys)
        {
            var rx = new Regex($@"^(\s*{Regex.Escape(key)}\s+)""?([^""\r\n\s]*)""?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            text = rx.Replace(text, m =>
            {
                string val = m.Groups[2].Value;
                if (!RustConfig.IsOn(val)) return m.Value;
                string off = char.IsDigit(val[0]) ? "0" : (char.IsUpper(val[0]) ? "False" : "false");
                Logger.Log($"  {key}: {val} -> {off}");
                changed++;
                return $"{m.Groups[1].Value}\"{off}\"";
            });
        }

        if (changed == 0)
        {
            Logger.Log("  none of the known keys were on, nothing changed.");
            return;
        }

        BackupOnce(cfg);
        File.WriteAllText(cfg, text);
        Logger.Log($"  {changed} setting(s) changed in {cfg}");
    }

    private static void AddDefenderExclusion()
    {
        string? dir = RustConfig.FindRustInstallDir();
        if (dir == null) { Logger.Log("  Rust folder not found."); return; }

        string output = Exec("powershell", $"-NoProfile -Command \"Add-MpPreference -ExclusionPath '{dir}'\"");
        Logger.Log("  powershell: " + (output.Length == 0 ? "(no output, probably OK)" : output));
        if (DefenderAvailable(out bool excluded, dir))
            Logger.Log(excluded ? "  exclusion confirmed." : "  exclusion not confirmed, check Windows Security manually.");
    }

    private static void OpenDriverPage()
    {
        string url = DriverChecker.Last?.Url is { Length: > 0 } u ? u : DriverChecker.NvidiaPage;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Logger.Log("  opened " + url);
    }

    // ================= "is it needed?" checks =================

    private static bool NeedsPower()
    {
        string now = Exec("powercfg", "/getactivescheme");
        return !now.Contains(HighPerfGuid, StringComparison.OrdinalIgnoreCase) &&
               !now.Contains(UltimateGuid, StringComparison.OrdinalIgnoreCase);
    }

    // missing value = Windows default = Game Mode on
    private static bool NeedsGameMode() =>
        ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") == 0;

    private static bool NeedsCapture() =>
        ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled") == 1 ||
        ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") == 1;

    private static bool NeedsTransparency() =>
        ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency") != 0;

    private static bool AnyRunning(HashSet<string> names)
    {
        foreach (var p in Process.GetProcesses())
        {
            try { if (names.Contains(p.ProcessName)) return true; }
            finally { p.Dispose(); }
        }
        return false;
    }

    private static bool NeedsGpuPref()
    {
        if (CurrentSpecs == null || !SystemChecks.HybridGpu(CurrentSpecs)) return false;
        string? dir = RustConfig.FindRustInstallDir();
        if (dir == null) return false;
        return GetGpuPreference(Path.Combine(dir, "RustClient.exe")) != "GpuPreference=2;";
    }

    private static bool NeedsRustPriority()
    {
        foreach (var p in Process.GetProcessesByName("RustClient"))
        {
            try { if (p.PriorityClass != ProcessPriorityClass.High) return true; }
            catch { return true; }
            finally { p.Dispose(); }
        }
        return false; // not running, or already High
    }

    private static bool NeedsRustQuality() => RustConfig.ReadGraphicsQuality() > 2;

    private static bool NeedsRustEffects()
    {
        var cfg = RustConfig.ReadAll();
        return cfg != null && RustConfig.HeavyKeys.Any(k => cfg.TryGetValue(k, out var v) && RustConfig.IsOn(v));
    }

    private static bool NeedsDefender()
    {
        string? dir = RustConfig.FindRustInstallDir();
        return dir != null && DefenderAvailable(out bool excluded, dir) && !excluded;
    }

    // ================= helpers used by SystemChecks too =================

    public static string? GetGpuPreference(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(GpuPrefPath);
        return key?.GetValue(exePath) as string;
    }

    /// <summary>False if Defender can't be queried (third-party antivirus, no admin...).</summary>
    public static bool DefenderAvailable(out bool excluded, string folder)
    {
        excluded = false;
        string output = Exec("powershell", "-NoProfile -Command \"(Get-MpPreference).ExclusionPath\"");
        if (output.Contains("Cannot", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("administrator", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Log("  Defender not queryable: " + output.Split('\n').FirstOrDefault());
            return false;
        }
        excluded = output.Contains(folder, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    public static List<StartupItem> FindStartupBloat(bool includeMachine)
    {
        var res = new List<StartupItem>();
        foreach (bool machine in includeMachine ? new[] { false, true } : new[] { false })
        {
            var root = machine ? Registry.LocalMachine : Registry.CurrentUser;
            using var run = root.OpenSubKey(RunPath);
            if (run == null) continue;
            using var approved = root.OpenSubKey(ApprovedPath);

            foreach (var name in run.GetValueNames())
            {
                if (!StartupBloat.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase))) continue;
                bool disabled = approved?.GetValue(name) is byte[] bytes && bytes.Length > 0 && (bytes[0] & 1) == 1;
                if (!disabled) res.Add(new StartupItem(machine, name));
            }
        }
        return res;
    }

    private static void BackupOnce(string file)
    {
        string bak = file + ".projectopti.bak";
        if (!File.Exists(bak))
        {
            File.Copy(file, bak);
            Logger.Log($"  backup saved: {bak}");
        }
        else Logger.Log($"  backup already exists (kept the original): {bak}");
    }

    private static void SetDword(RegistryKey root, string path, string name, int value)
    {
        using var key = root.CreateSubKey(path);
        object? old = key.GetValue(name);
        key.SetValue(name, value, RegistryValueKind.DWord);
        Logger.Log($"  {path}\\{name}: {old ?? "(not set)"} -> {value}");
    }

    private static int? ReadDword(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) is int i ? i : null;
    }

    public static string Exec(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (p == null) return "";
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }
}
