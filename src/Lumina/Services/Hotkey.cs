using System;
using System.Collections.Generic;
using System.Windows.Input;
using Lumina.Capture;
using CaptureMode = Lumina.Capture.CaptureMode;
using static Lumina.Native.NativeMethods;

namespace Lumina.Services;

[Flags]
public enum HotkeyModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// <summary>A global shortcut: modifiers plus one key (a virtual-key code), and the capture it starts.</summary>
public sealed class HotkeyBinding
{
    public HotkeyModifiers Modifiers { get; set; }
    public int Key { get; set; }
    public CaptureMode Mode { get; set; } = CaptureMode.Region;

    public static HotkeyBinding WinShiftS => new() { Modifiers = HotkeyModifiers.Win | HotkeyModifiers.Shift, Key = VK_S };
    public static HotkeyBinding WinShiftR => new() { Modifiers = HotkeyModifiers.Win | HotkeyModifiers.Shift, Key = VK_R, Mode = CaptureMode.Record };
    public static HotkeyBinding PrintScreen => new() { Key = VK_SNAPSHOT };

    public HotkeyBinding Clone() => new() { Modifiers = Modifiers, Key = Key, Mode = Mode };

    public bool SameKeys(HotkeyBinding other) => other.Modifiers == Modifiers && other.Key == Key;

    /// <summary>Key names in display order, e.g. ["Win", "Shift", "S"].</summary>
    public static List<string> Parts(HotkeyModifiers mods, int key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (mods.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (key != 0) parts.Add(KeyName(key));
        return parts;
    }

    public List<string> Parts() => Parts(Modifiers, Key);

    public override string ToString() => string.Join(" + ", Parts());

    /// <summary>Keys that make sense as a shortcut on their own, without a modifier.</summary>
    public static bool CanStandAlone(int vk)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key is System.Windows.Input.Key.Snapshot or System.Windows.Input.Key.Pause or System.Windows.Input.Key.Scroll
            || key is >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24;
    }

    public static bool IsModifierKey(int vk) =>
        vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN or >= 0xA0 and <= 0xA5;

    public static HotkeyModifiers ModifierOf(int vk) => vk switch
    {
        VK_SHIFT or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
        VK_CONTROL or 0xA2 or 0xA3 => HotkeyModifiers.Ctrl,
        VK_MENU or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
        VK_LWIN or VK_RWIN => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    public static string KeyName(int vk)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        switch (key)
        {
            case System.Windows.Input.Key.Snapshot: return "Print Screen";
            case System.Windows.Input.Key.Scroll: return "Scroll Lock";
            case System.Windows.Input.Key.Back: return "Backspace";
            case System.Windows.Input.Key.Return: return "Enter";
            case System.Windows.Input.Key.Escape: return "Esc";
            case System.Windows.Input.Key.Prior: return "Page Up";
            case System.Windows.Input.Key.Next: return "Page Down";
            case System.Windows.Input.Key.Capital: return "Caps Lock";
            case System.Windows.Input.Key.Delete: return "Del";
            case System.Windows.Input.Key.Insert: return "Ins";
            case System.Windows.Input.Key.Left: return "Left";
            case System.Windows.Input.Key.Right: return "Right";
            case System.Windows.Input.Key.Up: return "Up";
            case System.Windows.Input.Key.Down: return "Down";
        }
        if (key is >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9) return ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString();
        if (key is >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9) return "Num " + (key - System.Windows.Input.Key.NumPad0);
        // Punctuation follows the keyboard layout, so ask Windows what the key types.
        uint ch = MapVirtualKey((uint)vk, MAPVK_VK_TO_CHAR) & 0x7FFF;
        if (ch > 32) return char.ToUpperInvariant((char)ch).ToString();
        return key.ToString();
    }
}
