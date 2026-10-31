using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ProjectOpti;

public record DriverInfo(string Vendor, string Installed, string? Latest, bool? UpToDate, string Url, string Note)
{
    public string ToDisplayString()
    {
        string status = UpToDate switch
        {
            true => "up to date",
            false => "NEWER DRIVER AVAILABLE",
            _ => "could not compare"
        };
        return $"GPU driver ({Vendor}):{Environment.NewLine}" +
               $"  Installed: {Installed}{Environment.NewLine}" +
               $"  Latest:    {Latest ?? "unknown"}{Environment.NewLine}" +
               $"  Status:    {status}{Environment.NewLine}" +
               (string.IsNullOrEmpty(Note) ? "" : $"  Note:      {Note}{Environment.NewLine}") +
               (string.IsNullOrEmpty(Url) ? "" : $"  Download:  {Url}");
    }
}

public static class DriverChecker
{
    public const string NvidiaPage = "https://www.nvidia.com/Download/index.aspx";
    public const string AmdPage = "https://www.amd.com/en/support/download/drivers.html";

    public static DriverInfo? Last { get; private set; }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return c;
    }

    public static DriverInfo Check(SystemSpecs s)
    {
        Logger.Log($"Checking GPU driver ({s.GpuVendor}, installed {s.GpuDriverVersion})...");
        DriverInfo info;
        try
        {
            info = s.GpuVendor switch
            {
                "NVIDIA" => CheckNvidia(s),
                "AMD" => CheckAmd(s),
                _ => new DriverInfo(s.GpuVendor, s.GpuDriverVersion, null, null, "",
                    "Driver check supports NVIDIA and AMD only.")
            };
        }
        catch (Exception ex)
        {
            Logger.Log("  driver check failed: " + ex.Message);
            info = new DriverInfo(s.GpuVendor, s.GpuDriverVersion, null, null,
                s.GpuVendor == "AMD" ? AmdPage : NvidiaPage, "Could not check online: " + ex.Message);
        }

        Last = info;
        Logger.Log($"  driver result: installed {info.Installed}, latest {info.Latest ?? "unknown"}, upToDate={info.UpToDate?.ToString() ?? "unknown"}");
        return info;
    }

    // ---------------- NVIDIA ----------------
    // Uses NVIDIA's own download-finder endpoints (the ones community update scripts use).

    private static DriverInfo CheckNvidia(SystemSpecs s)
    {
        string installed = NvidiaVersionFromWmi(s.GpuDriverVersion);
        Logger.Log($"  NVIDIA installed version (converted from {s.GpuDriverVersion}): {installed}");

        string gpu = s.Gpu.Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase).Trim();
        bool laptop = gpu.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ||
                      gpu.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);

        var m = Regex.Match(gpu, @"(RTX|GTX|GT|MX)\s*(\d{3,4})", RegexOptions.IgnoreCase);
        if (!m.Success) throw new Exception($"GPU model not recognised: {gpu}");
        string digits = m.Groups[2].Value;
        string prefix = digits.Length == 4 ? digits[..2] : digits[..1] + "00";

        // 1 = GeForce product type -> series list
        var series = Lookup(2, "1");
        var serie = series.FirstOrDefault(x =>
            x.Name.Contains($"{prefix} Series", StringComparison.OrdinalIgnoreCase) &&
            x.Name.Contains("Notebook", StringComparison.OrdinalIgnoreCase) == laptop);
        if (serie.Name == null) throw new Exception($"No NVIDIA series found for '{prefix} Series'");
        Logger.Log($"  series: {serie.Name} (psid {serie.Value})");

        var products = Lookup(3, serie.Value);
        var product = products
            .Where(x => gpu.Contains(x.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Name.Length)
            .FirstOrDefault();
        if (product.Name == null) throw new Exception($"No NVIDIA product matched '{gpu}'");
        Logger.Log($"  product: {product.Name} (pfid {product.Value})");

        string osid = "57"; // Windows 10 64-bit fallback
        try
        {
            var oses = Lookup(4, product.Value);
            var os = oses.FirstOrDefault(x => x.Name.Contains("Windows 11", StringComparison.OrdinalIgnoreCase));
            if (os.Name == null)
                os = oses.FirstOrDefault(x => x.Name.Contains("Windows 10 64", StringComparison.OrdinalIgnoreCase));
            if (os.Name != null) osid = os.Value;
            Logger.Log($"  os: {os.Name ?? "fallback"} (osid {osid})");
        }
        catch (Exception ex) { Logger.Log("  OS lookup failed, using osid 57: " + ex.Message); }

        string url = $"https://www.nvidia.com/Download/processFind.aspx?psid={serie.Value}&pfid={product.Value}" +
                     $"&osid={osid}&lid=1&whql=1&ctk=0&dtcid=1&lang=en-us";
        Logger.Log("  GET " + url);
        string html = Get(url);

        var vm = Regex.Match(html, @"driverResults\.aspx/\d+/[a-z\-]+[^>]*>\s*([3-9]\d{2}\.\d{2})", RegexOptions.IgnoreCase);
        if (!vm.Success) vm = Regex.Match(html, @"\b([3-9]\d{2}\.\d{2})\b");
        if (!vm.Success) throw new Exception("could not find a version number in NVIDIA's response");

        string latest = vm.Groups[1].Value;
        bool? up = null;
        if (double.TryParse(installed, NumberStyles.Float, CultureInfo.InvariantCulture, out var i) &&
            double.TryParse(latest, NumberStyles.Float, CultureInfo.InvariantCulture, out var l))
            up = i >= l;

        return new DriverInfo("NVIDIA", installed, latest, up, NvidiaPage,
            "Latest = newest Game Ready driver NVIDIA lists for your card.");
    }

    // WMI "32.0.15.6094" -> NVIDIA version "560.94"
    private static string NvidiaVersionFromWmi(string wmi)
    {
        var parts = wmi.Split('.');
        if (parts.Length < 4) return wmi;
        string digits = parts[2] + parts[3];
        if (digits.Length > 5) digits = digits[^5..];
        return digits.Length == 5 ? $"{digits[..3]}.{digits[3..]}" : wmi;
    }

    private static List<(string Name, string Value)> Lookup(int typeId, string parentId)
    {
        string url = $"https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID={typeId}&ParentID={parentId}";
        Logger.Log("  GET " + url);
        var doc = XDocument.Parse(Get(url));
        return doc.Descendants("LookupValue")
            .Select(e => (Name: (string?)e.Element("Name") ?? "", Value: (string?)e.Element("Value") ?? ""))
            .Where(x => x.Name.Length > 0)
            .ToList();
    }

    // ---------------- AMD ----------------
    // AMD has no public API. Release-note pages have predictable URLs (RN-RAD-WIN-26-5-1)
    // and list the "Windows Driver Store Version", which we compare with the installed one.

    private static DriverInfo CheckAmd(SystemSpecs s)
    {
        string installed = s.GpuDriverVersion;

        var found = FindLatestAmdReleaseNotes();
        if (found == null)
            return new DriverInfo("AMD", installed, null, null, AmdPage,
                "Could not reach AMD release notes (amd.com may block automated requests). Check the page manually.");

        var (ver, html) = found.Value;
        var stores = Regex.Matches(html, @"Driver Store Version\s*(\d+\.\d+\.\d+\.\d+)")
            .Select(x => x.Groups[1].Value).Distinct().ToList();
        Logger.Log($"  latest AMD release notes: Adrenalin {ver}, driver store versions: {string.Join(", ", stores)}");

        string latestLabel = $"Adrenalin {ver}";
        if (stores.Count == 0)
            return new DriverInfo("AMD", installed, latestLabel, null, AmdPage,
                "Found the newest release notes but no driver version to compare against.");

        bool up = stores.Contains(installed);
        if (!up && Version.TryParse(installed, out var iv))
            up = iv >= stores.Select(Version.Parse).Max();

        return new DriverInfo("AMD", installed, latestLabel, up, AmdPage,
            "Installed = Windows driver version. Newest release notes may be an optional (non-WHQL) driver.");
    }

    private static (string Version, string Html)? FindLatestAmdReleaseNotes()
    {
        var now = DateTime.Now;
        for (int back = 0; back < 7; back++)
        {
            var d = now.AddMonths(-back);
            int yy = d.Year % 100, mm = d.Month;

            var tasks = Enumerable.Range(1, 6)
                .Select(n => TryGetAsync($"https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-{yy}-{mm}-{n}.html"))
                .ToArray();
            Task.WaitAll(tasks);

            for (int n = 6; n >= 1; n--)
            {
                var html = tasks[n - 1].Result;
                if (html != null) return ($"{yy}.{mm}.{n}", html);
            }
            Logger.Log($"  no AMD release notes found for {yy}.{mm}");
        }
        return null;
    }

    // ---------------- http ----------------

    private static string Get(string url)
    {
        using var r = Http.GetAsync(url).GetAwaiter().GetResult();
        r.EnsureSuccessStatusCode();
        return r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static async Task<string?> TryGetAsync(string url)
    {
        try
        {
            using var r = await Http.GetAsync(url);
            return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : null;
        }
        catch { return null; }
    }
}
