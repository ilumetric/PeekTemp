using PeekTemp.Native;
using static PeekTemp.Native.Win32;

namespace PeekTemp.UI;

/// <summary>
/// The widget itself: a per-pixel-alpha layered window created as a child of the taskbar
/// (Shell_TrayWnd), so it moves, auto-hides and hides for fullscreen apps together with the taskbar.
/// </summary>
internal sealed class TaskbarWidget : WindowBase, IDisposable
{
    private const string ClassName = "PeekTemp.TaskbarWidget";
    private const float TrayGapDip = 4;
    private const float LeftGapDip = 8;

    private readonly App _app;
    private readonly WidgetRenderer _renderer = new();
    private readonly LayeredSurface _surface = new();

    private IntPtr _taskbar;
    private WidgetRenderer.Layout? _layout;
    private RECT _bounds; // in taskbar client coordinates
    private float _scale = 1;

    private bool _hover;
    private bool _pressed;
    private bool _dragging;
    private POINT _pressCursor;
    private int _pressX;

    public TaskbarWidget(App app) => _app = app;

    public IntPtr Taskbar => _taskbar;

    public float Scale => _scale;

    /// <summary>Screen rectangle of the widget.</summary>
    public RECT ScreenBounds
    {
        get
        {
            GetWindowRect(Handle, out var r);
            return r;
        }
    }

    /// <summary>Makes sure the widget lives inside the current taskbar (Explorer may have restarted).</summary>
    public bool EnsureAttached()
    {
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            if (IsAlive) Destroy();
            return false;
        }
        if (IsAlive && taskbar == _taskbar && GetParent(Handle) == taskbar)
            return true;

        if (IsAlive) Destroy();
        _taskbar = taskbar;
        _layout = null;
        _bounds = default;
        _hover = _pressed = _dragging = false;

        RegisterClass(ClassName);
        CreateHandle(ClassName, WS_EX_LAYERED, WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 1, 1, taskbar);
        UpdateLayout(force: true);
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        Log.Info($"Attached to taskbar 0x{taskbar:X}, bounds {_bounds}, scale {_scale}");
        return true;
    }

    /// <summary>Recomputes size and position; cheap when nothing changed.</summary>
    public void UpdateLayout(bool force = false)
    {
        if (!IsAlive || !IsWindow(_taskbar)) return;

        GetClientRect(_taskbar, out var tb);
        var dpi = GetDpiForWindow(_taskbar);
        _scale = (dpi == 0 ? 96 : dpi) / 96f;

        bool horizontal = tb.Width >= tb.Height;
        var layout = _renderer.Compute(_scale, horizontal ? tb.Height : tb.Width, _app.Settings);

        int x, y;
        if (horizontal)
        {
            x = _dragging ? _bounds.Left : AnchoredX(tb, layout.Width);
            x = ClampX(tb, x, layout.Width);
            y = (tb.Height - layout.Height) / 2;
        }
        else
        {
            // Windows 11 has no vertical taskbar, but third-party tools can make one.
            x = Math.Max(0, (tb.Width - layout.Width) / 2);
            y = Math.Max(0, TrayEdge(tb, vertical: true) - layout.Height - (int)(TrayGapDip * _scale));
        }

        var bounds = new RECT { Left = x, Top = y, Right = x + layout.Width, Bottom = y + layout.Height };
        bool sizeChanged = _layout == null || _layout.Width != layout.Width || _layout.Height != layout.Height;
        _layout = layout;

        if (force || sizeChanged || !bounds.Equals(_bounds))
        {
            _bounds = bounds;
            SetWindowPos(Handle, HWND_TOP, x, y, layout.Width, layout.Height, SWP_NOACTIVATE);
            Render();
        }
        else if (GetWindow(Handle, GW_HWNDPREV) != IntPtr.Zero)
        {
            // Something (the XAML taskbar content) got above us: bring the widget back to the top.
            SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public void Render()
    {
        if (!IsAlive || _layout == null) return;
        using (var g = _surface.BeginDraw(_layout.Width, _layout.Height))
            _renderer.Draw(g, _layout, _app.Sensors.Current, _app.Settings, _app.Theme, _hover, _pressed);
        _surface.Present(Handle);
    }

    private int AnchoredX(RECT tb, int width)
    {
        var s = _app.Settings;
        int offset = (int)Math.Round(s.Offset * _scale);
        return s.Anchor == WidgetAnchor.Left
            ? (int)(LeftGapDip * _scale) + offset
            : TrayEdge(tb, vertical: false) - (int)(TrayGapDip * _scale) - width - offset;
    }

    private int ClampX(RECT tb, int x, int width)
    {
        int max = Math.Max(0, TrayEdge(tb, vertical: false) - width);
        return Math.Clamp(x, 0, max);
    }

    /// <summary>Left (or top) edge of the notification area in taskbar client coordinates.</summary>
    private int TrayEdge(RECT tb, bool vertical)
    {
        var tray = FindWindowExW(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var r) && r.Width > 0 && r.Height > 0)
        {
            var p = new POINT(r.Left, r.Top);
            ScreenToClient(_taskbar, ref p);
            return vertical ? p.Y : p.X;
        }
        return vertical ? tb.Height - (int)(200 * _scale) : tb.Width - (int)(320 * _scale);
    }

    /// <summary>Converts the dropped position to an anchor + offset so it survives tray width changes.</summary>
    private void CommitDrag()
    {
        if (_layout == null) return;
        GetClientRect(_taskbar, out var tb);
        var s = _app.Settings;
        int x = _bounds.Left;
        if (x + _layout.Width / 2 > tb.Width / 2)
        {
            s.Anchor = WidgetAnchor.Tray;
            s.Offset = (TrayEdge(tb, vertical: false) - (int)(TrayGapDip * _scale) - _layout.Width - x) / _scale;
        }
        else
        {
            s.Anchor = WidgetAnchor.Left;
            s.Offset = (x - (int)(LeftGapDip * _scale)) / _scale;
        }
        s.Offset = Math.Round(s.Offset, 1);
        _app.SaveSettings();
    }

    private void MoveTo(int x)
    {
        if (_layout == null) return;
        GetClientRect(_taskbar, out var tb);
        x = ClampX(tb, x, _layout.Width);
        if (x == _bounds.Left) return;
        _bounds = new RECT { Left = x, Top = _bounds.Top, Right = x + _layout.Width, Bottom = _bounds.Bottom };
        SetWindowPos(Handle, HWND_TOP, x, _bounds.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                // Clicking the widget must not take focus away from the active app.
                return MA_NOACTIVATE;

            case WM_MOUSEMOVE:
                if (!_hover)
                {
                    _hover = true;
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_LEAVE,
                        hwndTrack = Handle,
                    };
                    TrackMouseEvent(ref tme);
                    Render();
                }
                if (_pressed && GetCapture() == Handle)
                {
                    GetCursorPos(out var p);
                    int dx = p.X - _pressCursor.X;
                    if (!_dragging && Math.Abs(dx) >= Math.Max(4, GetSystemMetrics(SM_CXDRAG)))
                    {
                        _dragging = true;
                        _app.HideFlyout();
                    }
                    if (_dragging)
                    {
                        SetCursor(LoadCursorW(IntPtr.Zero, IDC_SIZEWE));
                        MoveTo(_pressX + dx);
                    }
                }
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                _hover = false;
                Render();
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                _pressed = true;
                _dragging = false;
                GetCursorPos(out _pressCursor);
                _pressX = _bounds.Left;
                SetCapture(Handle);
                Render();
                return IntPtr.Zero;

            case WM_LBUTTONUP:
            {
                bool wasPressed = _pressed;
                bool wasDragging = _dragging;
                _pressed = false;
                _dragging = false;
                ReleaseCapture();
                Render();
                if (wasDragging) CommitDrag();
                else if (wasPressed) _app.ToggleFlyout();
                return IntPtr.Zero;
            }

            case WM_CAPTURECHANGED:
                if (_pressed && lParam != Handle)
                {
                    bool wasDragging = _dragging;
                    _pressed = false;
                    _dragging = false;
                    Render();
                    if (wasDragging) CommitDrag();
                }
                return IntPtr.Zero;

            case WM_RBUTTONUP:
            {
                GetCursorPos(out var p);
                _app.ShowMenu(p);
                return IntPtr.Zero;
            }

            case WM_SETCURSOR:
                if (_dragging)
                {
                    SetCursor(LoadCursorW(IntPtr.Zero, IDC_SIZEWE));
                    return new IntPtr(1);
                }
                break;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    protected override void OnHandleDestroyed()
    {
        _layout = null;
        Log.Info("Widget window destroyed");
    }

    public void Dispose()
    {
        Destroy();
        _surface.Dispose();
        _renderer.Dispose();
    }
}
