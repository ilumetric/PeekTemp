using PeekTemp.Sensors;

namespace PeekTemp;

internal static class Program
{
    private const string MutexName = @"Local\PeekTemp.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        bool autostart = args.Contains(Autostart.AutostartArg);
        bool relaunched = args.Contains("--elevated");
        bool noElevate = args.Contains("--no-elevate");

        Log.Info($"Start {typeof(Program).Assembly.GetName().Version}, elevated={Elevation.IsElevated}, " +
                 $"args='{string.Join(' ', args)}', os={Environment.OSVersion.Version}");

        int preview = Array.IndexOf(args, "--preview");
        if (preview >= 0)
        {
            var dir = preview + 1 < args.Length ? args[preview + 1] : Path.Combine(AppContext.BaseDirectory, "preview");
            using var previewApp = new App(new Settings());
            previewApp.RenderPreview(dir);
            return 0;
        }

        if (args.Contains("--enable-autostart"))
            return Autostart.Enable() ? 0 : 1;

        // A restart (elevated relaunch or the task started from "restart as admin") waits for the old instance.
        bool replacing = relaunched || autostart;
        if (!replacing && IsAnotherInstanceRunning())
        {
            Log.Info("Another instance is running, exiting");
            return 0;
        }

        var settings = Settings.Load();

        // CPU temperature via PawnIO needs admin rights. The autostart task already runs elevated;
        // when it exists, starting it avoids the UAC prompt on manual launches too.
        if (!Elevation.IsElevated && !autostart && !relaunched && !noElevate && settings.AutoElevate &&
            LhmSource.IsPawnIoInstalled)
        {
            if (Autostart.TryRunTask() || Elevation.RelaunchElevated("--elevated"))
                return 0;
            Log.Info("Elevation declined, running without admin rights");
        }

        using var mutex = AcquireSingleInstance(replacing ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        if (mutex == null)
        {
            Log.Info("Another instance is running, exiting");
            return 0;
        }

        try
        {
            using var app = new App(settings);
            return app.Run();
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            return 1;
        }
        finally
        {
            Log.Info("Exit");
        }
    }

    private static bool IsAnotherInstanceRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var existing))
            {
                existing.Dispose();
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // exists, but owned by an elevated instance
        }
    }

    private static Mutex? AcquireSingleInstance(TimeSpan wait)
    {
        try
        {
            var mutex = new Mutex(false, MutexName);
            try
            {
                if (mutex.WaitOne(wait)) return mutex;
            }
            catch (AbandonedMutexException)
            {
                return mutex;
            }
            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
