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
        if (snap.CpuTempIssue != null)
            menu.Label("Темп. CPU недоступна: " + snap.CpuTempIssue);
        menu.Separator();

        menu.Item("Подробнее", ShowFlyout);

        var view = menu.Sub("Вид");
        view.Item("Две строки", () => Change(x => x.Layout = WidgetLayout.TwoRows), s.Layout == WidgetLayout.TwoRows);
        view.Item("Одна строка", () => Change(x => x.Layout = WidgetLayout.OneRow), s.Layout == WidgetLayout.OneRow);
        view.Separator();
        view.Item("Фон", () => Change(x => x.ShowBackground = !x.ShowBackground), s.ShowBackground);
        view.Item("Цвет по температуре", () => Change(x => x.ColorizeTemperature = !x.ColorizeTemperature), s.ColorizeTemperature);
        view.Separator();
        view.Item("Градусы Цельсия (°C)", () => Change(x => x.Fahrenheit = false), !s.Fahrenheit);
        view.Item("Градусы Фаренгейта (°F)", () => Change(x => x.Fahrenheit = true), s.Fahrenheit);

        var position = menu.Sub("Положение");
        position.Item("У трея (справа)", () => Change(x => { x.Anchor = WidgetAnchor.Tray; x.Offset = 0; }),
            s.Anchor == WidgetAnchor.Tray);
        position.Item("У левого края", () => Change(x => { x.Anchor = WidgetAnchor.Left; x.Offset = 0; }),
            s.Anchor == WidgetAnchor.Left);
        position.Separator();
        position.Item("Сбросить смещение", () => Change(x => x.Offset = 0), enabled: Math.Abs(s.Offset) > 0.01);
        position.Label("Виджет можно перетащить мышью");

        var interval = menu.Sub("Обновление");
        foreach (var (ms, label) in new[] { (500, "Каждые 0,5 с"), (1000, "Каждую секунду"), (2000, "Каждые 2 с"), (5000, "Каждые 5 с") })
            interval.Item(label, () => Change(x => x.UpdateIntervalMs = ms), s.UpdateIntervalMs == ms);

        var gpus = _sensors.GpuNames;
        if (gpus.Count > 1 || s.Gpu != null)
        {
            var gpu = menu.Sub("Видеокарта");
            gpu.Item("Автоматически (дискретная)", () => Change(x => x.Gpu = null), s.Gpu == null);
            foreach (var name in gpus)
                gpu.Item(name, () => Change(x => x.Gpu = name), string.Equals(s.Gpu, name, StringComparison.OrdinalIgnoreCase));
        }

        menu.Separator();
        menu.Item("Запускать при входе в Windows", ToggleAutostart, Autostart.IsEnabled);
        if (!Elevation.IsElevated)
            menu.Item("Перезапустить от имени администратора", RestartElevated);
        if (!_sensors.PawnIoInstalled)
            menu.Item("Установить драйвер PawnIO (для температуры CPU)…", InstallPawnIo);
        menu.Item("Открыть файл настроек", OpenSettingsFile);

        menu.Separator();
        menu.Item("Выход", Quit);

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
                "/k echo Установка драйвера PawnIO через winget... & winget install --id namazso.PawnIO -e & " +
                "echo. & echo Готово. Перезапустите PeekTemp от имени администратора.")
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
