using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cerebral.AiDictationPlugin;

public sealed partial class AiDictationPlugin
{
    private const int ControlsHotkeyId = 0x4A11;
    private Window? shortcutOwner;
    private HwndSource? shortcutSource;
    private nint shortcutHandle;

    private void AttachControlsShortcut(Window? owner)
    {
        if (!CallbacksAvailable || owner is null || ReferenceEquals(owner, shortcutOwner)) return;
        DetachControlsShortcut(); shortcutOwner = owner;
        owner.SourceInitialized += ShortcutSourceInitialized; owner.Closed += ShortcutOwnerClosed;
        RegisterControlsShortcut();
    }

    private void ShortcutSourceInitialized(object? sender, EventArgs args) => RegisterControlsShortcut();
    private void ShortcutOwnerClosed(object? sender, EventArgs args) => DetachControlsShortcut();

    private void RegisterControlsShortcut()
    {
        if (!CallbacksAvailable || shortcutOwner is null || shortcutHandle != 0) return;
        var handle = new WindowInteropHelper(shortcutOwner).Handle;
        if (handle == 0) return;
        var source = HwndSource.FromHwnd(handle);
        if (source is null) return;
        // The shortcut belongs to Cerebral's existing window. It adds no listener process,
        // and always opens controls rather than guessing an external insertion target.
        if (!RegisterHotKey(handle, ControlsHotkeyId, 0x4000 | 0x0001 | 0x0002, 0x44))
        {
            ReportHostStatus("Ctrl+Alt+D is unavailable. Open Dictation controls from the Cerebral tray or microphone button.");
            return;
        }
        shortcutHandle = handle; shortcutSource = source; source.AddHook(ControlsShortcutMessage);
    }

    private nint ControlsShortcutMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == ControlsHotkeyId && CallbacksAvailable)
        { handled = true; ShowDictationControls(); }
        return 0;
    }

    private void DetachControlsShortcut()
    {
        var handle = shortcutHandle; shortcutHandle = 0;
        var source = shortcutSource; shortcutSource = null;
        var owner = shortcutOwner; shortcutOwner = null;
        if (owner is not null)
        { owner.SourceInitialized -= ShortcutSourceInitialized; owner.Closed -= ShortcutOwnerClosed; }
        if (handle != 0) AttemptTeardown(() => { _ = UnregisterHotKey(handle, ControlsHotkeyId); });
        if (source is not null) AttemptTeardown(() => source.RemoveHook(ControlsShortcutMessage));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
