using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using static Lumina.Native.NativeMethods;

namespace Lumina.Services;

/// <summary>
/// Low-level keyboard hook that claims Win+Shift+S and/or Print Screen before Windows hands them to
/// Snipping Tool. Runs on its own thread so a busy UI thread can never make Windows drop the hook.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private LowLevelKeyboardProc? _proc;
    private bool _swallowSUp;
    private bool _snapshotDown;

    public volatile bool InterceptWinShiftS;
    public volatile bool InterceptPrintScreen;

    /// <summary>Raised on the UI thread when a capture shortcut is pressed.</summary>
    public event Action? CaptureRequested;

    public KeyboardHook(Dispatcher dispatcher) => _dispatcher = dispatcher;

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

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;

            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                bool win = IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
                bool shift = IsKeyDown(VK_SHIFT), ctrl = IsKeyDown(VK_CONTROL), alt = IsKeyDown(VK_MENU);

                if (k.vkCode == VK_S)
                {
                    if (down && InterceptWinShiftS && win && shift && !ctrl && !alt)
                    {
                        if (!_swallowSUp)
                        {
                            _swallowSUp = true;
                            // Without this, releasing Win afterwards opens the Start menu.
                            keybd_event(VK_DUMMY, 0, 0, UIntPtr.Zero);
                            keybd_event(VK_DUMMY, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            Raise();
                        }
                        return (IntPtr)1;
                    }
                    if (up && _swallowSUp)
                    {
                        _swallowSUp = false;
                        return (IntPtr)1;
                    }
                }
                else if (k.vkCode == VK_SNAPSHOT && InterceptPrintScreen && !win && !shift && !ctrl && !alt)
                {
                    // Some keyboards only report the key-up for Print Screen, so trigger on whichever comes first.
                    if (down)
                    {
                        if (!_snapshotDown) { _snapshotDown = true; Raise(); }
                        return (IntPtr)1;
                    }
                    if (up)
                    {
                        if (!_snapshotDown) Raise();
                        _snapshotDown = false;
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Raise() => _dispatcher.BeginInvoke(() => CaptureRequested?.Invoke());

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
