using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace PeekTemp;

internal static class Elevation
{
    private const int ErrorCancelled = 1223;

    public static bool IsElevated { get; } = CheckElevated();

    public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PeekTemp.exe");

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts a new elevated copy of the app. Returns false if the user declined the UAC prompt.</summary>
    public static bool RelaunchElevated(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExePath, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Elevated relaunch failed", ex);
            return false;
        }
    }

    /// <summary>Runs a program elevated (UAC prompt if needed), hidden, and waits for it. Returns the exit code or null.</summary>
    public static int? RunElevated(string file, string arguments, int timeoutMs = 30_000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            if (!IsElevated) psi.Verb = "runas";
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (!p.WaitForExit(timeoutMs)) return null;
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"RunElevated {file} failed", ex);
            return null;
        }
    }
}
