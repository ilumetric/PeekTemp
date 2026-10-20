using System.ComponentModel;
using System.Runtime.InteropServices;
using static PeekTemp.Native.Win32;

namespace PeekTemp.Native;

/// <summary>
/// Minimal raw Win32 window: registers a class once and routes messages to the instance.
/// All windows live on the UI thread, so the routing table needs no locking.
/// </summary>
internal abstract class WindowBase
{
    private static readonly Dictionary<IntPtr, WindowBase> s_windows = new();
    private static readonly HashSet<string> s_classes = new();
    private static readonly WndProc s_wndProc = StaticWndProc; // keep the delegate alive
    private static WindowBase? s_creating;

    public IntPtr Handle { get; private set; }

    public bool IsAlive => Handle != IntPtr.Zero && IsWindow(Handle);

    protected static void RegisterClass(string name, uint classStyle = 0)
    {
        if (s_classes.Contains(name)) return;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = classStyle,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = GetModuleHandleW(null),
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            lpszClassName = name,
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed for " + name);
        s_classes.Add(name);
    }

    protected IntPtr CreateHandle(string className, int exStyle, int style, int x, int y, int w, int h, IntPtr parent)
    {
        s_creating = this;
        try
        {
            var hwnd = CreateWindowExW(exStyle, className, "PeekTemp", style, x, y, w, h, parent,
                IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for " + className);
            Handle = hwnd;
            s_windows[hwnd] = this;
            return hwnd;
        }
        finally
        {
            s_creating = null;
        }
    }

    public void Destroy()
    {
        if (Handle != IntPtr.Zero && IsWindow(Handle))
            DestroyWindow(Handle);
    }

    protected virtual IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcW(Handle, msg, wParam, lParam);

    /// <summary>Called after the native window is gone (destroyed by us or by its parent's owner).</summary>
    protected virtual void OnHandleDestroyed() { }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!s_windows.TryGetValue(hWnd, out var window))
        {
            if (s_creating == null)
                return DefWindowProcW(hWnd, msg, wParam, lParam);
            window = s_creating;
            window.Handle = hWnd;
            s_windows[hWnd] = window;
        }

        IntPtr result;
        try
        {
            result = window.WndProc(msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // An exception must never cross the native boundary: that would kill the process.
            Log.Error($"WndProc 0x{msg:X4}", ex);
            result = DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        if (msg == WM_NCDESTROY)
        {
            s_windows.Remove(hWnd);
            window.Handle = IntPtr.Zero;
            try { window.OnHandleDestroyed(); }
            catch (Exception ex) { Log.Error("OnHandleDestroyed", ex); }
        }
        return result;
    }
}
