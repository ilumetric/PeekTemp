using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace PeekTemp;

/// <summary>
/// Start at logon through Task Scheduler with "highest privileges": unlike the Run registry key
/// this starts the app elevated without a UAC prompt, which CPU temperature (PawnIO) needs.
/// </summary>
internal static class Autostart
{
    public const string TaskName = "PeekTemp";
    public const string AutostartArg = "--autostart";

    private static volatile bool s_enabled;

    public static bool IsEnabled => s_enabled;

    /// <summary>Reads the task state (spawns schtasks, so call it off the UI thread).</summary>
    public static void Refresh()
    {
        var xml = QueryTaskXml();
        s_enabled = xml != null;
        if (xml == null) return;

        // Keep the task pointing at this exe if the app was moved (only possible when elevated).
        var command = Regex.Match(xml, "<Command>\"?(.*?)\"?</Command>").Groups[1].Value;
        bool readable = command.Length > 0 && command.IndexOfAny(new[] { '?', '�' }) < 0;
        if (Elevation.IsElevated && readable &&
            !string.Equals(command, Elevation.ExePath, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Autostart task points to '{command}', updating to '{Elevation.ExePath}'");
            Enable();
        }
    }

    public static bool Enable()
    {
        var file = Path.Combine(Path.GetTempPath(), "PeekTemp.task.xml");
        try
        {
            File.WriteAllText(file, BuildTaskXml(), Encoding.Unicode);
            var code = Elevation.RunElevated("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (code != 0) Log.Info($"schtasks /Create exit code: {code?.ToString() ?? "cancelled"}");
        }
        finally
        {
            try { File.Delete(file); } catch { /* ignore */ }
        }
        Refresh();
        return s_enabled;
    }

    /// <summary>
    /// Starts the app through the autostart task: it runs with highest privileges, so this gives
    /// admin rights without a UAC prompt. Returns false if there is no such task.
    /// </summary>
    public static bool TryRunTask()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Run /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            Log.Info($"schtasks /Run exit code: {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Error("schtasks /Run failed", ex);
            return false;
        }
    }

    public static bool Disable()
    {
        var code = Elevation.RunElevated("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
        if (code != 0) Log.Info($"schtasks /Delete exit code: {code?.ToString() ?? "cancelled"}");
        Refresh();
        return !s_enabled;
    }

    private static string? QueryTaskXml()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            Log.Error("schtasks /Query failed", ex);
            return null;
        }
    }

    private static string BuildTaskXml()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(Elevation.ExePath);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(Elevation.ExePath) ?? "");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>PeekTemp: CPU/GPU temperature and load in the taskbar</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT3S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{exe}"</Command>
                  <Arguments>{AutostartArg}</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
