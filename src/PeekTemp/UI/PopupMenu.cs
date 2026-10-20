using static PeekTemp.Native.Win32;

namespace PeekTemp.UI;

/// <summary>Native Win32 popup menu (gets the Windows 11 look and dark mode for free).</summary>
internal sealed class PopupMenu : IDisposable
{
    private readonly List<Action> _actions;
    private readonly List<PopupMenu> _children = new();

    public PopupMenu() : this(new List<Action>()) { }

    private PopupMenu(List<Action> actions)
    {
        _actions = actions;
        Handle = CreatePopupMenu();
    }

    public IntPtr Handle { get; }

    public void Item(string text, Action action, bool isChecked = false, bool enabled = true)
    {
        _actions.Add(action);
        uint flags = MF_STRING | (isChecked ? MF_CHECKED : 0) | (enabled ? 0 : MF_GRAYED);
        AppendMenuW(Handle, flags, new IntPtr(_actions.Count), text);
    }

    public void Label(string text) => AppendMenuW(Handle, MF_STRING | MF_GRAYED, IntPtr.Zero, text);

    public void Separator() => AppendMenuW(Handle, MF_SEPARATOR, IntPtr.Zero, null);

    public PopupMenu Sub(string text)
    {
        var child = new PopupMenu(_actions);
        _children.Add(child);
        AppendMenuW(Handle, MF_POPUP, child.Handle, text);
        return child;
    }

    /// <summary>Shows the menu at a screen point and runs the chosen action.</summary>
    public void Show(IntPtr owner, POINT pt)
    {
        // Without foreground the menu would not close when clicking elsewhere.
        SetForegroundWindow(owner);
        int id = TrackPopupMenuEx(Handle,
            TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_LEFTALIGN,
            pt.X, pt.Y, owner, IntPtr.Zero);
        PostMessageW(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        if (id > 0 && id <= _actions.Count)
            _actions[id - 1]();
    }

    public void Dispose()
    {
        // Destroying the root menu destroys its submenus too.
        DestroyMenu(Handle);
    }
}
