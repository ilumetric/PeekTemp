using System.Drawing;
using System.Drawing.Imaging;
using static PeekTemp.Native.Win32;

namespace PeekTemp.Native;

/// <summary>
/// A premultiplied 32-bit DIB that GDI+ draws into directly and that is pushed to a
/// per-pixel-alpha layered window with UpdateLayeredWindow.
/// </summary>
internal sealed class LayeredSurface : IDisposable
{
    private IntPtr _dc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private Bitmap? _bitmap;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The current frame (premultiplied ARGB), e.g. for saving a preview.</summary>
    public Bitmap? Bitmap => _bitmap;

    public Graphics BeginDraw(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_bitmap == null || width != Width || height != Height)
            Allocate(width, height);

        var g = Graphics.FromImage(_bitmap!);
        g.Clear(Color.Transparent);
        return g;
    }

    /// <summary>Pushes the bitmap to the window. <paramref name="screenPos"/> is only for top-level windows.</summary>
    public void Present(IntPtr hwnd, POINT? screenPos = null, byte opacity = 255)
    {
        if (_bitmap == null) return;

        var size = new SIZE(Width, Height);
        var src = new POINT(0, 0);
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            SourceConstantAlpha = opacity,
            AlphaFormat = AC_SRC_ALPHA,
        };

        bool ok;
        if (screenPos is { } p)
        {
            var dst = p;
            ok = UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, _dc, ref src, 0, ref blend, ULW_ALPHA);
        }
        else
        {
            ok = UpdateLayeredWindow(hwnd, IntPtr.Zero, IntPtr.Zero, ref size, _dc, ref src, 0, ref blend, ULW_ALPHA);
        }

        if (!ok)
            Log.Throttled("ulw", $"UpdateLayeredWindow failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    private void Allocate(int width, int height)
    {
        Release();

        var screen = GetDC(IntPtr.Zero);
        try
        {
            _dc = CreateCompatibleDC(screen);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }

        var bmi = new BITMAPINFOHEADER
        {
            biSize = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down, so the GDI+ stride is positive
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(_dc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
            throw new InvalidOperationException("CreateDIBSection failed");
        _oldBitmap = SelectObject(_dc, _dib);

        _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        Width = width;
        Height = height;
    }

    private void Release()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        if (_dc != IntPtr.Zero)
        {
            SelectObject(_dc, _oldBitmap);
            DeleteDC(_dc);
            _dc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero)
        {
            DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
        Width = Height = 0;
    }

    public void Dispose() => Release();
}
