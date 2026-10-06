using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ParadisCapture.Interop;

namespace ParadisCapture.Services;

public enum HotkeyAction { StartStop, PauseResume }

/// <summary>
/// System-wide hotkeys via RegisterHotKey, so they work while a game has focus. Hotkeys that
/// another application already owns simply fail to register; the caller reports that once.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyAction> _registered = new();

    public event Action<HotkeyAction>? Pressed;

    public HotkeyService(Window window)
    {
        _source = (HwndSource)PresentationSource.FromVisual(window)!;
        _hwnd = _source.Handle;
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Registers the given hotkeys, replacing any previous ones. Returns the ones that couldn't be
    /// registered (already taken by another application).
    /// </summary>
    public IReadOnlyList<(HotkeyAction Action, string Text)> Apply(IEnumerable<(HotkeyAction Action, string Text)> hotkeys)
    {
        UnregisterAll();
        var failed = new List<(HotkeyAction, string)>();
        foreach (var (action, text) in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!Hotkey.TryParse(text, out uint modifiers, out uint key))
            {
                Log.Warn($"Hotkey \"{text}\" could not be understood and was ignored");
                failed.Add((action, text));
                continue;
            }

            int id = (int)action + 1;
            if (NativeMethods.RegisterHotKey(_hwnd, id, modifiers | NativeMethods.MOD_NOREPEAT, key))
            {
                _registered[id] = action;
                Log.Info($"Hotkey {text} registered for {action}");
            }
            else
            {
                Log.Warn($"Hotkey {text} is already in use by another application");
                failed.Add((action, text));
            }
        }
        return failed;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }
        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        foreach (int id in _registered.Keys) NativeMethods.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}

/// <summary>Parsing and formatting of hotkey text like "Ctrl+Shift+F9".</summary>
public static class Hotkey
{
    public static bool TryParse(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "win" or "windows": modifiers |= NativeMethods.MOD_WIN; break;
                default:
                    if (virtualKey != 0) return false; // more than one non-modifier key
                    if (!Enum.TryParse<Key>(raw, ignoreCase: true, out var key) || key == Key.None) return false;
                    virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }

        // A bare key would hijack normal typing.
        return virtualKey != 0 && modifiers != 0;
    }

    /// <summary>Formats a WPF key press as hotkey text, or null if it isn't a usable combination.</summary>
    public static string? Format(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None) return null;
        if (modifiers == ModifierKeys.None) return null;

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
