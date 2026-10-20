using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using PeekTemp.Sensors;

namespace PeekTemp.UI;

/// <summary>Measures and paints the compact taskbar widget.</summary>
internal sealed class WidgetRenderer : IDisposable
{
    private const string Family = "Segoe UI";
    private const string FamilySemibold = "Segoe UI Semibold";

    private readonly Bitmap _measureBitmap = new(1, 1);
    private readonly Graphics _measure;
    private readonly StringFormat _near;
    private readonly StringFormat _far;
    private float _fontPx = -1;
    private Font? _labelFont;
    private Font? _tempFont;
    private Font? _loadFont;

    public sealed class Layout
    {
        public int Width;
        public int Height;
        public bool TwoRows;
        public float PadX;
        public float LabelW;
        public float TempW;
        public float LoadW;
        public float Gap;
        public float GroupGap;
        public float GroupW;
        public float LineH;
        public float Radius;
    }

    public WidgetRenderer()
    {
        _measure = Graphics.FromImage(_measureBitmap);
        _measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _near = MakeFormat(StringAlignment.Near);
        _far = MakeFormat(StringAlignment.Far);
    }

    private static StringFormat MakeFormat(StringAlignment alignment)
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        f.Alignment = alignment;
        return f;
    }

    public Layout Compute(float scale, int taskbarHeightPx, Settings s)
    {
        float tbDip = taskbarHeightPx / scale;
        bool two = s.Layout == WidgetLayout.TwoRows;

        // Two rows fill the taskbar like the clock; one row is a pill like the search box.
        float hDip = two
            ? Math.Min(tbDip - (tbDip >= 40 ? 8 : 4), 40)
            : Math.Min(tbDip - (tbDip >= 40 ? 16 : 6), 32);
        hDip = Math.Max(hDip, 16);
        float fontDip = two
            ? Math.Clamp((hDip - 6) / 2f * 0.72f, 8.5f, 12f)
            : Math.Clamp(hDip * 0.38f, 9f, 12.5f);
        EnsureFonts(fontDip * scale);

        var l = new Layout
        {
            TwoRows = two,
            Height = (int)Math.Round(hDip * scale),
            LabelW = Math.Max(Measure("CPU", _labelFont!), Measure("GPU", _labelFont!)),
            TempW = Measure(s.Fahrenheit ? "200°" : "100°", _tempFont!),
            LoadW = Measure("100%", _loadFont!),
            Gap = 6 * scale,
            GroupGap = 14 * scale,
            PadX = (s.ShowBackground ? (two ? 10 : 14) : 6) * scale,
            LineH = (float)Math.Round(fontDip * scale * 1.36f),
        };
        l.GroupW = l.LabelW + l.Gap + l.TempW + l.Gap + l.LoadW;
        float content = two ? l.GroupW : l.GroupW * 2 + l.GroupGap;
        l.Width = (int)Math.Ceiling(content + l.PadX * 2);
        l.Radius = two ? 6 * scale : l.Height / 2f;
        return l;
    }

    public void Draw(Graphics g, Layout l, SensorSnapshot snap, Settings s, Theme t, bool hover, bool pressed)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // Background. Fully transparent pixels would let clicks fall through to the taskbar,
        // so the area is always covered with at least an invisible fill.
        using (var hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
            g.FillRectangle(hit, 0, 0, l.Width, l.Height);

        Color? fill = pressed ? t.FillPressed : hover ? t.FillHover : s.ShowBackground ? t.Fill : null;
        var rect = new RectangleF(0.5f, 0.5f, l.Width - 1, l.Height - 1);
        if (fill is { } f)
        {
            using var path = RoundedRect(rect, l.Radius);
            using var brush = new SolidBrush(f);
            g.FillPath(brush, path);
            if (s.ShowBackground)
            {
                using var pen = new Pen(t.Stroke, 1);
                g.DrawPath(pen, path);
            }
        }

        if (l.TwoRows)
        {
            float cy1 = l.Height / 2f - l.LineH / 2f;
            float cy2 = l.Height / 2f + l.LineH / 2f;
            DrawGroup(g, l, t, s, l.PadX, cy1, "CPU", snap.CpuTemp, snap.CpuLoad, s.CpuWarm, s.CpuHot);
            DrawGroup(g, l, t, s, l.PadX, cy2, "GPU", snap.GpuTemp, snap.GpuLoad, s.GpuWarm, s.GpuHot);
        }
        else
        {
            float cy = l.Height / 2f;
            DrawGroup(g, l, t, s, l.PadX, cy, "CPU", snap.CpuTemp, snap.CpuLoad, s.CpuWarm, s.CpuHot);
            DrawGroup(g, l, t, s, l.PadX + l.GroupW + l.GroupGap, cy, "GPU", snap.GpuTemp, snap.GpuLoad, s.GpuWarm, s.GpuHot);
        }
    }

    private void DrawGroup(Graphics g, Layout l, Theme t, Settings s, float x, float cy,
        string label, float? temp, float? load, double warm, double hot)
    {
        DrawText(g, label, _labelFont!, t.TextTertiary, x, l.LabelW, cy, _near);

        x += l.LabelW + l.Gap;
        var tempColor = temp == null ? t.TextTertiary : t.TemperatureColor(temp, warm, hot, s.ColorizeTemperature);
        DrawText(g, Format.Temp(temp, s.Fahrenheit), _tempFont!, tempColor, x, l.TempW, cy, _far);

        x += l.TempW + l.Gap;
        DrawText(g, Format.Percent(load), _loadFont!, load == null ? t.TextTertiary : t.TextSecondary, x, l.LoadW, cy, _far);
    }

    /// <summary>Draws text so that its capital/digit height is centered on <paramref name="centerY"/>.</summary>
    private static void DrawText(Graphics g, string text, Font font, Color color, float x, float width, float centerY,
        StringFormat format)
    {
        var family = font.FontFamily;
        float em = family.GetEmHeight(font.Style);
        float ascent = font.Size * family.GetCellAscent(font.Style) / em;
        float capHeight = font.Size * 0.70f; // Segoe UI cap/digit height
        float baseline = (float)Math.Round(centerY + capHeight / 2f);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, new RectangleF(x, baseline - ascent, width, font.Size * 2), format);
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private float Measure(string text, Font font) =>
        _measure.MeasureString(text, font, PointF.Empty, _near).Width;

    private void EnsureFonts(float px)
    {
        if (Math.Abs(px - _fontPx) < 0.01f) return;
        DisposeFonts();
        _fontPx = px;
        _labelFont = new Font(FamilySemibold, px * 0.9f, FontStyle.Regular, GraphicsUnit.Pixel);
        _tempFont = new Font(FamilySemibold, px, FontStyle.Regular, GraphicsUnit.Pixel);
        _loadFont = new Font(Family, px, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private void DisposeFonts()
    {
        _labelFont?.Dispose();
        _tempFont?.Dispose();
        _loadFont?.Dispose();
    }

    public void Dispose()
    {
        DisposeFonts();
        _near.Dispose();
        _far.Dispose();
        _measure.Dispose();
        _measureBitmap.Dispose();
    }
}
