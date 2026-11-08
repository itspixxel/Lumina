using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Lumina.Capture;
using static Lumina.Native.NativeMethods;

namespace Lumina.Services;

/// <summary>
/// Low-level keyboard hook that claims the user's capture shortcuts (Win+Shift+S by default) before Windows
/// hands them to Snipping Tool, and records new shortcuts for Settings. Runs on its own thread so a busy UI
/// thread can never make Windows drop the hook.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private LowLevelKeyboardProc? _proc;
    private volatile HotkeyBinding[] _bindings = Array.Empty<HotkeyBinding>();
    private volatile bool _recording;
    private int _swallowUp; // key whose key-up belongs to a shortcut we claimed

    /// <summary>Raised on the UI thread when a capture shortcut is pressed.</summary>
    public event Action<CaptureMode>? CaptureRequested;

    /// <summary>
    /// Raised on the UI thread while recording: the modifiers held and, once a non-modifier key is pressed,
    /// that key (0 until then).
    /// </summary>
    public event Action<HotkeyModifiers, int>? RecordProgress;

    public KeyboardHook(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void SetBindings(IEnumerable<HotkeyBinding> bindings) =>
        _bindings = bindings.Where(b => b.Key != 0).Select(b => b.Clone()).ToArray();

    /// <summary>While recording, every key press goes to <see cref="RecordProgress"/> and no shortcut fires.</summary>
    public bool Recording
    {
        get => _recording;
        set => _recording = value;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Lumina.KeyboardHook" };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Info($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(_hook);
    }

    private static HotkeyModifiers HeldModifiers()
    {
        var mods = HotkeyModifiers.None;
        if (IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)) mods |= HotkeyModifiers.Win;
        if (IsKeyDown(VK_CONTROL)) mods |= HotkeyModifiers.Ctrl;
        if (IsKeyDown(VK_MENU)) mods |= HotkeyModifiers.Alt;
        if (IsKeyDown(VK_SHIFT)) mods |= HotkeyModifiers.Shift;
        return mods;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam, vk = (int)k.vkCode;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;

            if ((k.flags & LLKHF_INJECTED) == 0 && (down || up))
            {
                // The key-up of a key we claimed, even if the modifiers were let go first.
                if (up && vk == _swallowUp)
                {
                    _swallowUp = 0;
                    return (IntPtr)1;
                }

                var mods = HeldModifiers();
                if (HotkeyBinding.IsModifierKey(vk))
                {
                    // Modifiers always pass through (swallowing them can leave them stuck down).
                    if (_recording)
                    {
                        // Key state doesn't include this event yet.
                        var flag = HotkeyBinding.ModifierOf(vk);
                        mods = down ? mods | flag : mods & ~flag;
                        Post(mods, 0);
                    }
                }
                else if (_recording)
                {
                    if (down)
                    {
                        Claim(vk, mods);
                        Post(mods, vk);
                        return (IntPtr)1;
                    }
                }
                else if (Match(vk, mods) is { } binding)
                {
                    if (down)
                    {
                        if (_swallowUp != vk) // ignore auto-repeat
                        {
                            Claim(vk, mods);
                            Raise(binding.Mode);
                        }
                        return (IntPtr)1;
                    }
                    // Some keyboards only report the key-up for Print Screen, so trigger on that instead.
                    if (vk == VK_SNAPSHOT)
                    {
                        Raise(binding.Mode);
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Claim(int vk, HotkeyModifiers mods)
    {
        _swallowUp = vk;
        // Without this, releasing Win afterwards opens the Start menu, and releasing Alt focuses the menu bar.
        if ((mods & (HotkeyModifiers.Win | HotkeyModifiers.Alt)) != 0)
        {
            keybd_event(VK_DUMMY, 0, 0, UIntPtr.Zero);
            keybd_event(VK_DUMMY, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }

    private HotkeyBinding? Match(int vk, HotkeyModifiers? mods = null)
    {
        foreach (var b in _bindings)
            if (b.Key == vk && (mods == null || b.Modifiers == mods)) return b;
        return null;
    }

    private void Raise(CaptureMode mode) => _dispatcher.BeginInvoke(() => CaptureRequested?.Invoke(mode));

    private void Post(HotkeyModifiers mods, int vk) => _dispatcher.BeginInvoke(() => RecordProgress?.Invoke(mods, vk));

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
