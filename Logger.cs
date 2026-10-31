using System.IO;

namespace ProjectOpti;

public static class AppPaths
{
    // Desktop\Project opti  (the project itself lives in here)
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Project opti");

    // Desktop\Project opti\logs
    public static readonly string Logs = Path.Combine(Root, "logs");
}

public static class Logger
{
    private static readonly object Lock = new();
    private static string? _file;

    public static event Action<string>? LineLogged;

    public static void Init()
    {
        // Creates the folder on first run; does nothing if it already exists.
        Directory.CreateDirectory(AppPaths.Logs);
        _file = Path.Combine(AppPaths.Logs, $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
        Log("Log file: " + _file);
    }

    public static void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        lock (Lock)
        {
            try
            {
                if (_file != null)
                    File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch { /* never crash because of logging */ }
        }

        LineLogged?.Invoke(line);
    }
}
