using System.Text;

namespace PeekTemp;

/// <summary>Tiny append-only log in %LOCALAPPDATA%\PeekTemp\peektemp.log.</summary>
internal static class Log
{
    private const long MaxSize = 512 * 1024;
    private static readonly object s_lock = new();
    private static readonly Dictionary<string, DateTime> s_throttle = new();
    private static string? s_path;

    public static string Path => s_path ??= Init();

    private static string Init()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeekTemp");
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "peektemp.log");
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxSize)
                File.Move(path, path + ".old", overwrite: true);
        }
        catch
        {
            // ignore: logging must never break the app
        }
        return path;
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    /// <summary>Logs a recurring message at most once per minute per key.</summary>
    public static void Throttled(string key, string message)
    {
        lock (s_lock)
        {
            var now = DateTime.UtcNow;
            if (s_throttle.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1))
                return;
            s_throttle[key] = now;
        }
        Write("WARN", message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (s_lock)
                File.AppendAllText(Path, line, Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }
}
