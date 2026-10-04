using System.Runtime.InteropServices;

namespace MobDisplayController.UI;

/// <summary>
/// A context menu that keeps its place at the top of the topmost band, the way the shell's own windows
/// do - the tray icon's menu is used while the pointer is on the taskbar, and the taskbar, the Start
/// menu and the icon flyouts are topmost windows that put themselves back on top as soon as anything
/// is inserted above them.
///
/// Raising the menu (SetWindowPos with HWND_TOPMOST) is not enough on its own: the shell answers every
/// raise of ours with a raise of its own, because it re-asserts its topmost state the same way this
/// class does, and the last window to be processed in an operation wins. What this class adds is that
/// same re-assertion: whenever Windows is about to change this window's position in the z-order - i.e.
/// when the taskbar or the Start menu inserts itself above the menu - the insert-after window is forced
/// back to HWND_TOPMOST, so the menu comes out on top of it.
/// </summary>
internal sealed class TopMostMenu : ContextMenuStrip
{
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_ACTIVATE = 0x0006;

    /// <summary>WM_ACTIVATE's wParam low word when the window is being deactivated.</summary>
    private const int WA_INACTIVE = 0;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_WINDOWPOSCHANGING && m.LParam != IntPtr.Zero)
            ForceTopMost(m.LParam);

        // WinForms dismisses a drop-down that loses the activation, and the shell's own panel can hold the
        // activation for as long as it is open (measured: the Start menu kept it, and this menu was closed again
        // about 40 ms after it had been shown). Swallowing the deactivation is what keeps the menu usable: with
        // the mouse capture taken when it opens (WindowZOrder.TakeMouseCapture), the mouse reaches the menu
        // whoever is active - which is how a native menu behaves, and how the tray menus of other applications
        // keep working in the same situation.
        if (m.Msg == WM_ACTIVATE && (m.WParam.ToInt64() & 0xFFFF) == WA_INACTIVE)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Rewrites the WINDOWPOS the system is about to apply: <c>hwndInsertAfter</c> is the window this one
    /// is being placed below, and HWND_TOPMOST means "no window at all, in the topmost band".
    /// </summary>
    private static void ForceTopMost(IntPtr windowPos)
    {
        var pos = Marshal.PtrToStructure<WINDOWPOS>(windowPos);
        if (pos.hwndInsertAfter == HWND_TOPMOST)
            return;

        pos.hwndInsertAfter = HWND_TOPMOST;
        // The struct belongs to the sender of the message, so it must not be freed here.
        Marshal.StructureToPtr(pos, windowPos, fDeleteOld: false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }
}
