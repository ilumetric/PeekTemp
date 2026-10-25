using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using PeekTemp.Native;
using PeekTemp.Sensors;
using static PeekTemp.Native.Win32;

namespace PeekTemp.UI;

/// <summary>Details panel shown above the widget on click: values, power, memory and a 2-minute history.</summary>
internal sealed class Flyout : WindowBase, IDisposable
{
    private const string ClassName = "PeekTemp.Flyout";
    private const float WidthDip = 304;
    private const float PadDip = 16;
    private const float ShadowDip = 16;
    private const float RadiusDip = 8;
    private const float GraphHeightDip = 46;
    private const float AppRowDip = 20;

    private readonly App _app;
    private readonly LayeredSurface _surface = new();
    private readonly float[] _buffer = new float[SensorService.HistoryLength];
    private readonly float[] _buffer2 = new float[SensorService.HistoryLength];
    private RECT _anchor;
    private RECT _taskbarRect;
    private float _scale = 1;
    private float _fontScale = -1;
    private Font? _title, _body, _bodyStrong, _small, _big;
    private DateTime _hiddenAt;
    private readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.OrdinalIgnoreCase);

    public Flyout(App app) => _app = app;

    public bool Visible { get; private set; }

    public void Toggle(RECT anchor, RECT taskbarRect, float scale)
    {
        if (Visible)
        {
            Hide();
            return;
        }
        // The click that deactivated (and so closed) the flyout must not reopen it.
        if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 300) return;
        Show(anchor, taskbarRect, scale);
    }

    public void Show(RECT anchor, RECT taskbarRect, float scale)
    {
        if (!IsAlive)
        {
            RegisterClass(ClassName);
            CreateHandle(ClassName, WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST, WS_POPUP, 0, 0, 1, 1, IntPtr.Zero);
        }
        _anchor = anchor;
        _taskbarRect = taskbarRect;
        _scale = scale;
        Visible = true;
        _app.Sensors.Processes.Active = true;
        _app.Sensors.Wake();
        Render();
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetForegroundWindow(Handle);
    }

    public void Hide()
    {
        if (!Visible) return;
        Visible = false;
        _app.Sensors.Processes.Active = false;
        _hiddenAt = DateTime.UtcNow;
        ClearIcons();
        if (IsAlive) ShowWindow(Handle, SW_HIDE);
    }

    public void Refresh()
    {
        if (Visible && IsAlive) Render();
    }

    protected override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ACTIVATE:
                if (LoWord(wParam) == WA_INACTIVE) Hide();
                return IntPtr.Zero;
            case WM_KEYDOWN:
                if ((int)wParam == VK_ESCAPE) Hide();
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                GetCursorPos(out var p);
                Hide();
                _app.ShowMenu(p);
                return IntPtr.Zero;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    private float D(float dip) => dip * _scale;

    private (float BodyW, float BodyH, float Shadow) Paint()
    {
        EnsureFonts();
        var snap = _app.Sensors.Current;
        var processes = _app.Sensors.Processes;
        var s = _app.Settings;
        var t = _app.Theme;

        float pad = D(PadDip);
        float bodyW = (float)Math.Round(D(WidthDip));
        float innerW = bodyW - pad * 2;

        string footer = BuildFooter(snap);
        float footerH;
        using (var mg = Graphics.FromHwnd(IntPtr.Zero))
        {
            mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            footerH = mg.MeasureString(footer, _small!, (int)innerW).Height;
        }

        float sectionH = SectionHeight(isGpu: false);
        float bodyH = (float)Math.Ceiling(pad + sectionH + D(29) + SectionHeight(isGpu: true) + D(14) + footerH + pad);
        float shadow = D(ShadowDip);
        int w = (int)Math.Ceiling(bodyW + shadow * 2);
        int h = (int)Math.Ceiling(bodyH + shadow * 2);

        using (var g = _surface.BeginDraw(w, h))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var body = new RectangleF(shadow, shadow, bodyW, bodyH);
            DrawShadow(g, body, t);
            using (var path = WidgetRenderer.RoundedRect(body, D(RadiusDip)))
            using (var fill = new SolidBrush(t.FlyoutBackground))
            using (var pen = new Pen(t.FlyoutStroke, 1))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            float x = body.X + pad;
            float y = body.Y + pad;
            y = DrawSection(g, x, y, innerW, Strings.Processor, snap.CpuName, snap.CpuTemp, snap.CpuLoad,
                new[] { (Strings.Power, Format.Watts(snap.CpuPower)) },
                _app.Sensors.CpuTemp, _app.Sensors.CpuLoad, s.CpuWarm, s.CpuHot);
            y = DrawApps(g, x, y, innerW, processes.TopCpu, processes.Measured);

            y += D(14);
            using (var pen = new Pen(t.Divider, 1))
                g.DrawLine(pen, body.X + 1, (float)Math.Round(y) + 0.5f, body.Right - 1, (float)Math.Round(y) + 0.5f);
            y += D(15);

            y = DrawSection(g, x, y, innerW, Strings.GraphicsCard, snap.GpuName, snap.GpuTemp, snap.GpuLoad,
                new[] { (Strings.Power, Format.Watts(snap.GpuPower)), (Strings.Memory, Format.Memory(snap.GpuMemUsedMb, snap.GpuMemTotalMb)) },
                _app.Sensors.GpuTemp, _app.Sensors.GpuLoad, s.GpuWarm, s.GpuHot);
            y = DrawApps(g, x, y, innerW, processes.TopGpu, processes.Measured);

            y += D(14);
            using var brush = new SolidBrush(t.TextTertiary);
            g.DrawString(footer, _small!, brush, new RectangleF(x, y, innerW, footerH + D(4)));
        }
        return (bodyW, bodyH, shadow);
    }

    /// <summary>Paints the flyout off-screen at the given scale (used by --preview).</summary>
    public Bitmap PaintPreview(float scale)
    {
        _scale = scale;
        Paint();
        return (Bitmap)_surface.Bitmap!.Clone();
    }

    private void Render()
    {
        var (bodyW, bodyH, shadow) = Paint();

        // Above the taskbar (or below a top taskbar), horizontally centered on the widget.
        var monitor = MonitorFromWindow(_app.Widget.Taskbar, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref mi);
        var area = mi.rcMonitor;

        int bodyX = (_anchor.Left + _anchor.Right) / 2 - (int)(bodyW / 2);
        int margin = (int)D(8);
        bodyX = Math.Clamp(bodyX, area.Left + margin, Math.Max(area.Left + margin, area.Right - margin - (int)bodyW));
        bool below = (_taskbarRect.Top + _taskbarRect.Bottom) / 2 < (area.Top + area.Bottom) / 2;
        int bodyY = below
            ? _taskbarRect.Bottom + (int)D(12)
            : _taskbarRect.Top - (int)D(12) - (int)bodyH;

        _surface.Present(Handle, new POINT(bodyX - (int)shadow, bodyY - (int)shadow));
    }

    private float SectionHeight(bool isGpu)
    {
        int stats = isGpu ? 3 : 2;
        return D(18) + D(17) + D(10) + Math.Max(D(44), stats * D(19)) + D(12) + D(GraphHeightDip) + D(6) + D(14)
               + AppsHeight();
    }

    private float AppsHeight() => D(10) + D(17) + ProcessMonitor.TopCount * D(AppRowDip);

    /// <summary>The applications that load this device the most: icon, name and share.</summary>
    private float DrawApps(Graphics g, float x, float y, float w, IReadOnlyList<AppUsage> apps, bool measured)
    {
        var t = _app.Theme;
        using var text = new SolidBrush(t.TextSecondary);
        using var strong = new SolidBrush(t.Text);
        using var tertiary = new SolidBrush(t.TextTertiary);
        using var nearFmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            LineAlignment = StringAlignment.Center,
        };
        using var farFmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Far,
            FormatFlags = StringFormatFlags.NoWrap,
            LineAlignment = StringAlignment.Center,
        };

        y += D(10);
        g.DrawString(Strings.TopApps, _small!, tertiary, new RectangleF(x, y, w, D(17)), nearFmt);
        y += D(17);

        float rowH = D(AppRowDip);
        if (apps.Count == 0)
        {
            g.DrawString(measured ? Strings.NoActiveApps : Strings.Measuring, _body!, tertiary,
                new RectangleF(x, y, w, rowH), nearFmt);
            return y + ProcessMonitor.TopCount * rowH;
        }

        float icon = D(16);
        float percentW = D(44);
        foreach (var app in apps)
        {
            var iconRect = new RectangleF(x, y + (rowH - icon) / 2, icon, icon);
            if (Icon(app.Path) is { } bitmap)
            {
                var mode = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bitmap, iconRect);
                g.InterpolationMode = mode;
            }
            else
            {
                using var path = WidgetRenderer.RoundedRect(RectangleF.Inflate(iconRect, -D(1), -D(1)), D(3));
                using var fill = new SolidBrush(t.Divider);
                g.FillPath(fill, path);
            }

            float nameX = x + icon + D(8);
            g.DrawString(app.Name, _body!, text, new RectangleF(nameX, y, w - (nameX - x) - percentW, rowH), nearFmt);
            g.DrawString(Format.Percent(Math.Max(1, app.Percent)), _bodyStrong!, strong,
                new RectangleF(x + w - percentW, y, percentW, rowH), farFmt);
            y += rowH;
        }
        return y + (ProcessMonitor.TopCount - apps.Count) * rowH;
    }

    private Bitmap? Icon(string? path)
    {
        if (path == null) return null;
        if (_icons.TryGetValue(path, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            bitmap = icon?.ToBitmap();
        }
        catch (Exception ex)
        {
            Log.Throttled("icon", "Icon extraction failed: " + ex.Message);
        }
        if (_icons.Count > 256) ClearIcons();
        _icons[path] = bitmap;
        return bitmap;
    }

    private void ClearIcons()
    {
        foreach (var bitmap in _icons.Values) bitmap?.Dispose();
        _icons.Clear();
    }

    private float DrawSection(Graphics g, float x, float y, float w, string title, string? name,
        float? temp, float? load, (string Label, string Value)[] extra, History tempHistory, History loadHistory,
        double warm, double hot)
    {
        var t = _app.Theme;
        var s = _app.Settings;
        using var text = new SolidBrush(t.Text);
        using var secondary = new SolidBrush(t.TextSecondary);
        using var tertiary = new SolidBrush(t.TextTertiary);
        using var nearFmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var farFmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Far,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        g.DrawString(title, _title!, text, new RectangleF(x, y, w, D(18)), nearFmt);
        y += D(18);
        g.DrawString(name ?? "—", _small!, tertiary, new RectangleF(x, y, w, D(17)), nearFmt);
        y += D(17) + D(10);

        // Big temperature on the left, stats on the right.
        var stats = new List<(string, string)> { (Strings.Load, Format.Percent(load)) };
        stats.AddRange(extra);
        float rowH = Math.Max(D(44), stats.Count * D(19));
        var tempColor = temp == null ? t.TextTertiary : t.TemperatureColor(temp, warm, hot, s.ColorizeTemperature);
        using (var big = new SolidBrush(tempColor))
            g.DrawString(Format.Temp(temp, s.Fahrenheit) + (temp == null ? "" : s.Fahrenheit ? "F" : "C"), _big!, big,
                new PointF(x - D(1), y + (rowH - _big!.Size * 1.33f) / 2), StringFormat.GenericTypographic);

        float statsX = x + w * 0.46f;
        float statsW = w - (statsX - x);
        float sy = y + (rowH - stats.Count * D(19)) / 2;
        foreach (var (label, value) in stats)
        {
            g.DrawString(label, _body!, secondary, new RectangleF(statsX, sy, statsW, D(19)), nearFmt);
            g.DrawString(value, _bodyStrong!, text, new RectangleF(statsX, sy, statsW, D(19)), farFmt);
            sy += D(19);
        }
        y += rowH + D(12);

        // History graph: load as a filled area, temperature as a line.
        var graph = new RectangleF(x, y, w, D(GraphHeightDip));
        DrawGraph(g, graph, tempHistory, loadHistory);
        y += graph.Height + D(6);

        // Legend
        float lx = x;
        lx = DrawLegend(g, lx, y, t.GraphTemp, Strings.Temperature);
        DrawLegend(g, lx + D(14), y, t.GraphLoad, Strings.Load);
        var span = TimeSpan.FromMilliseconds((double)SensorService.HistoryLength * _app.Settings.UpdateIntervalMs);
        var spanText = span.TotalMinutes >= 1
            ? span.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + Strings.Minutes
            : span.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " " + Strings.Seconds;
        g.DrawString(spanText.Replace('.', Strings.DecimalSeparator), _small!, tertiary, new RectangleF(x, y - D(2), w, D(16)), farFmt);
        return y + D(14);
    }

    private float DrawLegend(Graphics g, float x, float y, Color color, string label)
    {
        using (var b = new SolidBrush(color))
            g.FillEllipse(b, x, y + D(3), D(7), D(7));
        using var tb = new SolidBrush(_app.Theme.TextTertiary);
        g.DrawString(label, _small!, tb, x + D(11), y - D(2), StringFormat.GenericTypographic);
        return x + D(11) + g.MeasureString(label, _small!, PointF.Empty, StringFormat.GenericTypographic).Width;
    }

    private void DrawGraph(Graphics g, RectangleF r, History tempHistory, History loadHistory)
    {
        var t = _app.Theme;
        using (var path = WidgetRenderer.RoundedRect(r, D(4)))
        using (var bg = new SolidBrush(t.GraphGrid))
            g.FillPath(bg, path);

        using (var grid = new Pen(t.GraphGrid, 1) { DashStyle = DashStyle.Dot })
            g.DrawLine(grid, r.X, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2);

        int n = _buffer.Length;
        float Step(int i) => r.X + r.Width * i / (n - 1);

        var inner = RectangleF.Inflate(r, 0, -D(2));

        // Load 0..100 %
        loadHistory.CopyTo(_buffer);
        foreach (var seg in Segments(_buffer))
        {
            var pts = new List<PointF> { new(Step(seg.Start), inner.Bottom) };
            for (int i = seg.Start; i < seg.End; i++)
                pts.Add(new PointF(Step(i), inner.Bottom - inner.Height * Math.Clamp(_buffer[i], 0, 100) / 100f));
            pts.Add(new PointF(Step(seg.End - 1), inner.Bottom));
            using var fill = new SolidBrush(Color.FromArgb(t.Dark ? 56 : 44, t.GraphLoad));
            g.FillPolygon(fill, pts.ToArray());
            using var pen = new Pen(Color.FromArgb(180, t.GraphLoad), D(1.1f)) { LineJoin = LineJoin.Round };
            if (pts.Count > 3) g.DrawLines(pen, pts.Skip(1).Take(pts.Count - 2).ToArray());
        }

        // Temperature: 20..100 °C, the top grows if it runs hotter.
        tempHistory.CopyTo(_buffer2);
        float max = 100;
        foreach (var v in _buffer2)
            if (!float.IsNaN(v) && v > max) max = (float)Math.Ceiling(v / 10) * 10;
        const float min = 20;
        foreach (var seg in Segments(_buffer2))
        {
            if (seg.End - seg.Start < 2) continue;
            var pts = new PointF[seg.End - seg.Start];
            for (int i = seg.Start; i < seg.End; i++)
                pts[i - seg.Start] = new PointF(Step(i),
                    inner.Bottom - inner.Height * Math.Clamp((_buffer2[i] - min) / (max - min), 0, 1));
            using var pen = new Pen(t.GraphTemp, D(1.6f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, pts);
        }
    }

    private static IEnumerable<(int Start, int End)> Segments(float[] values)
    {
        int start = -1;
        for (int i = 0; i <= values.Length; i++)
        {
            bool valid = i < values.Length && !float.IsNaN(values[i]);
            if (valid && start < 0) start = i;
            else if (!valid && start >= 0)
            {
                yield return (start, i);
                start = -1;
            }
        }
    }

    private void DrawShadow(Graphics g, RectangleF body, Theme t)
    {
        float size = D(ShadowDip);
        const int layers = 12;
        int baseAlpha = t.Dark ? 9 : 5;
        for (int i = layers; i >= 1; i--)
        {
            float k = i / (float)layers;
            var r = RectangleF.Inflate(body, size * k, size * k);
            r.Offset(0, D(3) * (1 - k * 0.5f));
            int alpha = (int)(baseAlpha * (1 - k) * (1 - k) * 2.2f) + 1;
            using var path = WidgetRenderer.RoundedRect(r, D(RadiusDip) + size * k);
            using var b = new SolidBrush(Color.FromArgb(Math.Min(255, alpha), 0, 0, 0));
            g.FillPath(b, path);
        }
    }

    private string BuildFooter(SensorSnapshot snap)
    {
        var lines = new List<string>();
        if (snap.CpuTempIssue is { } issue)
            lines.Add(Strings.CpuTempUnavailable + Strings.Issue(issue) + ".");
        var source = string.IsNullOrEmpty(snap.Source) ? Strings.NoData : snap.Source;
        lines.Add(Strings.Source + source + (Elevation.IsElevated ? Strings.Administrator : ""));
        lines.Add(Strings.FlyoutHint);
        return string.Join("\n", lines);
    }

    private void EnsureFonts()
    {
        if (Math.Abs(_fontScale - _scale) < 0.001f) return;
        DisposeFonts();
        _fontScale = _scale;
        _title = new Font("Segoe UI Semibold", D(13), FontStyle.Regular, GraphicsUnit.Pixel);
        _body = new Font("Segoe UI", D(12.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _bodyStrong = new Font("Segoe UI Semibold", D(12.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _small = new Font("Segoe UI", D(11.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _big = new Font("Segoe UI Semibold", D(30), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private void DisposeFonts()
    {
        _title?.Dispose();
        _body?.Dispose();
        _bodyStrong?.Dispose();
        _small?.Dispose();
        _big?.Dispose();
    }

    public void Dispose()
    {
        Destroy();
        _surface.Dispose();
        DisposeFonts();
        ClearIcons();
    }
}
