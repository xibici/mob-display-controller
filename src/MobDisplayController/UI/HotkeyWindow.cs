using System.Windows.Forms;
using MobDisplayController.Native;

namespace MobDisplayController.UI;

/// <summary>
/// Invisible window that owns a single system-wide hotkey. Used for the display recovery
/// shortcut, which has to be usable when the screens are showing nothing at all - so it
/// can't live in the tray menu.
/// </summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HWND_MESSAGE = -3;
    private const int HotkeyId = 1;

    public event Action? Pressed;

    public HotkeyWindow(uint modifiers, uint virtualKey)
    {
        CreateHandle(new CreateParams { Parent = (IntPtr)HWND_MESSAGE });
        Hotkeys.RegisterHotKey(Handle, HotkeyId, modifiers, virtualKey);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Hotkeys.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            Pressed?.Invoke();

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Hotkeys.UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }
}
