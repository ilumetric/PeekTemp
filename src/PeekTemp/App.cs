using System.Diagnostics;
using System.Drawing;
using PeekTemp.Native;
using PeekTemp.Sensors;
using PeekTemp.UI;
using static PeekTemp.Native.Win32;

namespace PeekTemp;

/// <summary>
/// Hidden top-level host window: runs the message loop, receives broadcasts (Explorer restart,
/// theme and display changes), owns the menu and wires the widget, flyout and sensors together.
/// </summary>
internal sealed class App : WindowBase, IDisposable
{
    private const string ClassName = "PeekTemp.Host";
    private const uint WM_APP_SENSORS = WM_APP + 1;
    private static readonly IntPtr TimerLayout = new(1);

    private readonly SensorService _sensors;
    private readonly TaskbarWidget _widget;
    private readonly Flyout _flyout;
    private uint _wmTaskbarCreated;
    private int _sensorPosted;
    private DateTime _settingsWriteTime;

    public App(Settings settings)
    {
        Settings = settings;
        Strings.Apply(settings.Language);
        _sensors = new SensorService(OnSensorsUpdated)
        {
            IntervalMs = settings.UpdateIntervalMs,
            PreferredGpu = settings.Gpu,
        };
        _widget = new TaskbarWidget(this);
        _flyout = new Flyout(this);
    }

    public Settings Settings { get; private set; }

    public SensorService Sensors => _sensors;

    public TaskbarWidget Widget => _widget;

    public Theme Theme { get; private set; } = Theme.Current;

    public int Run()
    {
        RegisterClass(ClassName);
        CreateHandle(ClassName, WS_EX_TOOLWINDOW, WS_POPUP, 0, 0, 0, 0, IntPtr.Zero);

        // Explorer broadcasts this after a restart. An elevated process has to opt in to receive it.
        _wmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        ChangeWindowMessageFilterEx(Handle, _wmTaskbarCreated, MSGFLT_ALLOW, IntPtr.Zero);

        SetMenuTheme(Theme.Dark);
        _settingsWriteTime = SettingsFileTime();
        _sensors.Start();
        _widget.EnsureAttached();
        SetTimer(Handle, TimerLayout, 1000, IntPtr.Zero);
        Task.Run(Autostart.Refresh);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return 0;
    }

    public void Quit()
    {
        if (IsAlive) DestroyWindow(Handle);
    }

    public void SaveSettings()
    {
        Settings.Save();
        _settingsWriteTime = SettingsFileTime();
    }

    private void OnSensorsUpdated()
    {
        // Called on the sensor thread; coalesce into a single pending message.
        if (Interlocked.Exchange(ref _sensorPosted, 1) == 0)
            PostMessageW(Handle, WM_APP_SENSORS, IntPtr.Zero, IntPtr.Zero);
    }

    protected override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _wmTaskbarCreated && msg != 0)
        {
            Log.Info("TaskbarCreated: re-attaching");
            _flyout.Hide();
            _widget.EnsureAttached();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_APP_SENSORS:
                Interlocked.Exchange(ref _sensorPosted, 0);
                _widget.Render();
                _flyout.Refresh();
                return IntPtr.Zero;

            case WM_TIMER when wParam == TimerLayout:
                ReloadSettingsIfEdited();
                if (_widget.EnsureAttached())
                    _widget.UpdateLayout();
                return IntPtr.Zero;

            case WM_SETTINGCHANGE:
            case WM_THEMECHANGED:
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                OnEnvironmentChanged();
                break;

            case WM_QUERYENDSESSION:
                return new IntPtr(1);

            case WM_CLOSE:
                Quit();
                return IntPtr.Zero;

            case WM_DESTROY:
                KillTimer(Handle, TimerLayout);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    private void OnEnvironmentChanged()
    {
        var theme = Theme.Current;
        if (theme != Theme)
        {
            Theme = theme;
            SetMenuTheme(theme.Dark);
            _flyout.Hide();
        }
        if (_widget.EnsureAttached())
            _widget.UpdateLayout(force: true);
    }

    private static DateTime SettingsFileTime()
    {
        try { return File.GetLastWriteTimeUtc(Settings.FilePath); }
        catch { return default; }
    }

    /// <summary>Picks up manual edits of settings.json (thresholds and so on) without a restart.</summary>
    private void ReloadSettingsIfEdited()
    {
        var time = SettingsFileTime();
        if (time == _settingsWriteTime) return;
        _settingsWriteTime = time;
        Log.Info("settings.json changed on disk, reloading");
        Settings = Settings.Load();
        ApplySettings();
    }

    private void ApplySettings()
    {
        _sensors.IntervalMs = Settings.UpdateIntervalMs;
        _sensors.PreferredGpu = Settings.Gpu;
        Strings.Apply(Settings.Language);
        _widget.UpdateLayout(force: true);
        _flyout.Refresh();
    }

    private void Change(Action<Settings> change)
    {
        change(Settings);
        SaveSettings();
        ApplySettings();
    }

    public void ToggleFlyout()
    {
        if (!_widget.IsAlive) return;
        GetWindowRect(_widget.Taskbar, out var taskbar);
        _flyout.Toggle(_widget.ScreenBounds, taskbar, _widget.Scale);
    }

    private void ShowFlyout()
    {
        if (!_widget.IsAlive || _flyout.Visible) return;
        GetWindowRect(_widget.Taskbar, out var taskbar);
        _flyout.Show(_widget.ScreenBounds, taskbar, _widget.Scale);
    }

    public void HideFlyout() => _flyout.Hide();

    public void ShowMenu(POINT pt)
    {
        var snap = _sensors.Current;
        var s = Settings;
        using var menu = new PopupMenu();

        menu.Label("CPU: " + (snap.CpuName ?? "—"));
        menu.Label("GPU: " + (snap.GpuName ?? "—"));
        if (snap.CpuTempIssue is { } issue)
            menu.Label(Strings.CpuTempUnavailableShort + Strings.Issue(issue));
        menu.Separator();

        menu.Item(Strings.Details, ShowFlyout);

        var view = menu.Sub(Strings.View);
        view.Item(Strings.TwoRows, () => Change(x => x.Layout = WidgetLayout.TwoRows), s.Layout == WidgetLayout.TwoRows);
        view.Item(Strings.OneRow, () => Change(x => x.Layout = WidgetLayout.OneRow), s.Layout == WidgetLayout.OneRow);
        view.Separator();
        view.Item(Strings.Background, () => Change(x => x.ShowBackground = !x.ShowBackground), s.ShowBackground);
        view.Item(Strings.ColorByTemperature, () => Change(x => x.ColorizeTemperature = !x.ColorizeTemperature), s.ColorizeTemperature);
        view.Separator();
        view.Item(Strings.Celsius, () => Change(x => x.Fahrenheit = false), !s.Fahrenheit);
        view.Item(Strings.Fahrenheit, () => Change(x => x.Fahrenheit = true), s.Fahrenheit);

        var position = menu.Sub(Strings.Position);
        position.Item(Strings.NearTray, () => Change(x => { x.Anchor = WidgetAnchor.Tray; x.Offset = 0; }),
            s.Anchor == WidgetAnchor.Tray);
        position.Item(Strings.LeftEdge, () => Change(x => { x.Anchor = WidgetAnchor.Left; x.Offset = 0; }),
            s.Anchor == WidgetAnchor.Left);
        position.Separator();
        position.Item(Strings.ResetOffset, () => Change(x => x.Offset = 0), enabled: Math.Abs(s.Offset) > 0.01);
        position.Label(Strings.DragHint);

        var interval = menu.Sub(Strings.Refresh);
        foreach (var (ms, label) in new[] { (500, Strings.Every05s), (1000, Strings.Every1s), (2000, Strings.Every2s), (5000, Strings.Every5s) })
            interval.Item(label, () => Change(x => x.UpdateIntervalMs = ms), s.UpdateIntervalMs == ms);

        var gpus = _sensors.GpuNames;
        if (gpus.Count > 1 || s.Gpu != null)
        {
            var gpu = menu.Sub(Strings.Gpu);
            gpu.Item(Strings.GpuAuto, () => Change(x => x.Gpu = null), s.Gpu == null);
            foreach (var name in gpus)
                gpu.Item(name, () => Change(x => x.Gpu = name), string.Equals(s.Gpu, name, StringComparison.OrdinalIgnoreCase));
        }

        var language = menu.Sub(Strings.LanguageMenu);
        language.Item(Strings.LanguageSystem, () => Change(x => x.Language = Language.System), s.Language == Language.System);
        language.Item("Русский", () => Change(x => x.Language = Language.Russian), s.Language == Language.Russian);
        language.Item("English", () => Change(x => x.Language = Language.English), s.Language == Language.English);

        menu.Separator();
        menu.Item(Strings.StartWithWindows, ToggleAutostart, Autostart.IsEnabled);
        if (!Elevation.IsElevated)
            menu.Item(Strings.RestartAsAdmin, RestartElevated);
        if (!_sensors.PawnIoInstalled)
            menu.Item(Strings.InstallPawnIo, InstallPawnIo);
        menu.Item(Strings.OpenSettings, OpenSettingsFile);

        menu.Separator();
        menu.Item(Strings.Exit, Quit);

        _flyout.Hide();
        menu.Show(Handle, pt);
    }

    private static void ToggleAutostart()
    {
        bool enable = !Autostart.IsEnabled;
        // schtasks may show a UAC prompt and is waited for, so keep it off the UI thread.
        Task.Run(() =>
        {
            bool ok = enable ? Autostart.Enable() : Autostart.Disable();
            Log.Info($"Autostart {(enable ? "enable" : "disable")}: {(ok ? "ok" : "failed")}");
        });
    }

    private void RestartElevated()
    {
        if (Autostart.TryRunTask() || Elevation.RelaunchElevated("--elevated"))
            Quit();
    }

    private static void InstallPawnIo()
    {
        try
        {
            // A visible console, so the user sees winget's progress and its own prompts.
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/k echo {Strings.PawnIoInstalling} & winget install --id namazso.PawnIO -e & echo. & echo {Strings.PawnIoDone}")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("winget launch failed, opening the website", ex);
            try { Process.Start(new ProcessStartInfo("https://pawnio.eu/") { UseShellExecute = true }); }
            catch (Exception ex2) { Log.Error("Failed to open pawnio.eu", ex2); }
        }
    }

    private void OpenSettingsFile()
    {
        SaveSettings();
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to open settings file", ex);
        }
    }

    /// <summary>
    /// --preview: renders the widget variants and the flyout with live sensor data into PNG files,
    /// without touching the taskbar or the saved settings.
    /// </summary>
    public void RenderPreview(string dir)
    {
        Directory.CreateDirectory(dir);
        _sensors.IntervalMs = 250;
        _sensors.Start();
        Thread.Sleep(10_000);

        var snap = _sensors.Current;
        File.WriteAllText(Path.Combine(dir, "snapshot.txt"),
            $"{snap}\nGPUs: {string.Join(", ", _sensors.GpuNames)}\nPawnIO: {_sensors.PawnIoInstalled}, elevated: {Elevation.IsElevated}\n");

        const float scale = 1.5f;
        const int taskbarHeight = 72;
        using var renderer = new WidgetRenderer();
        using var surface = new LayeredSurface();
        var variants = new (WidgetLayout Layout, bool Background, bool Hover)[]
        {
            (WidgetLayout.TwoRows, true, false),
            (WidgetLayout.TwoRows, true, true),
            (WidgetLayout.TwoRows, false, false),
            (WidgetLayout.TwoRows, false, true),
            (WidgetLayout.OneRow, true, false),
            (WidgetLayout.OneRow, false, false),
        };

        foreach (var theme in new[] { Theme.DarkTheme, Theme.LightTheme })
        {
            Theme = theme;
            var name = theme.Dark ? "dark" : "light";
            var taskbarColor = theme.Dark ? Color.FromArgb(28, 28, 28) : Color.FromArgb(238, 238, 238);

            using var sheet = new Bitmap(520, taskbarHeight * variants.Length + 8 * (variants.Length + 1));
            using (var sg = Graphics.FromImage(sheet))
            {
                sg.Clear(theme.Dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(180, 180, 180));
                for (int i = 0; i < variants.Length; i++)
                {
                    var v = variants[i];
                    Settings.Layout = v.Layout;
                    Settings.ShowBackground = v.Background;
                    var layout = renderer.Compute(scale, taskbarHeight, Settings);
                    using (var g = surface.BeginDraw(layout.Width, layout.Height))
                        renderer.Draw(g, layout, snap, Settings, theme, v.Hover, pressed: false);

                    int top = 8 + i * (taskbarHeight + 8);
                    using (var strip = new SolidBrush(taskbarColor))
                        sg.FillRectangle(strip, 0, top, sheet.Width, taskbarHeight);
                    sg.DrawImage(surface.Bitmap!, 24, top + (taskbarHeight - layout.Height) / 2);
                }
            }
            sheet.Save(Path.Combine(dir, $"widget-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);

            Settings.Layout = WidgetLayout.TwoRows;
            Settings.ShowBackground = true;
            using var flyout = _flyout.PaintPreview(scale);
            using var canvas = new Bitmap(flyout.Width, flyout.Height);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(theme.Dark ? Color.FromArgb(36, 52, 72) : Color.FromArgb(196, 212, 230));
                g.DrawImage(flyout, 0, 0);
            }
            canvas.Save(Path.Combine(dir, $"flyout-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    public void Dispose()
    {
        _sensors.Dispose();
        _flyout.Dispose();
        _widget.Dispose();
        // Settings are saved on every change; saving here would pin today's defaults into the file.
    }
}
