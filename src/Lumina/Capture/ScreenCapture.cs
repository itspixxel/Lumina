using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumina.Native;
using static Lumina.Native.NativeMethods;

namespace Lumina.Capture;

/// <summary>A frozen copy of the whole virtual desktop in physical pixels (BGRA, opaque).</summary>
public sealed class ScreenSnapshot
{
    public Int32Rect Bounds { get; }
    public byte[] Pixels { get; }
    public int Stride => Bounds.Width * 4;

    public ScreenSnapshot(Int32Rect bounds, byte[] pixels)
    {
        Bounds = bounds;
        Pixels = pixels;
    }

    public BitmapSource Crop(Int32Rect rect)
    {
        rect = RectUtil.Intersect(rect, Bounds);
        if (rect.Width <= 0 || rect.Height <= 0) rect = new Int32Rect(Bounds.X, Bounds.Y, 1, 1);

        int x0 = rect.X - Bounds.X, y0 = rect.Y - Bounds.Y;
        int stride = rect.Width * 4;
        var buffer = new byte[stride * rect.Height];
        for (int row = 0; row < rect.Height; row++)
            Buffer.BlockCopy(Pixels, (y0 + row) * Stride + x0 * 4, buffer, row * stride, stride);

        var bmp = BitmapSource.Create(rect.Width, rect.Height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
        bmp.Freeze();
        return bmp;
    }

    public Color GetPixel(int x, int y)
    {
        x -= Bounds.X;
        y -= Bounds.Y;
        if (x < 0 || y < 0 || x >= Bounds.Width || y >= Bounds.Height) return Colors.Black;
        int i = y * Stride + x * 4;
        return Color.FromRgb(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }
}

public static class ScreenCapture
{
    public static Int32Rect VirtualScreen => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static ScreenSnapshot CaptureVirtualScreen(bool includeCursor)
    {
        var bounds = VirtualScreen;
        int w = bounds.Width, h = bounds.Height;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memDc, dib);
        try
        {
            BitBlt(memDc, 0, 0, w, h, screenDc, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
            if (includeCursor) DrawCursor(memDc, bounds.X, bounds.Y);
            GdiFlush();

            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // GDI leaves the alpha byte undefined; screenshots are always opaque.
            var span = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
            for (int i = 0; i < span.Length; i++) span[i] |= 0xFF000000;
            return new ScreenSnapshot(bounds, pixels);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    internal static void DrawCursor(IntPtr hdc, int originX, int originY)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0) return;
        if (!GetIconInfo(ci.hCursor, out var info)) return;
        DrawIconEx(hdc, ci.ptScreenPos.X - info.xHotspot - originX, ci.ptScreenPos.Y - info.yHotspot - originY,
            ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
    }
}

public sealed record MonitorInfo(Int32Rect Bounds, Int32Rect WorkArea, bool IsPrimary)
{
    public static List<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(handle, ref mi))
                list.Add(new MonitorInfo(RectUtil.From(mi.rcMonitor), RectUtil.From(mi.rcWork), (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}

public sealed class WindowInfo
{
    private string? _processName;

    public IntPtr Handle { get; init; }
    public Int32Rect Bounds { get; init; }
    public string Title { get; init; } = "";
    public uint ProcessId { get; init; }
    public string? ProcessName => _processName ??= WindowFinder.GetProcessName(ProcessId);
}

public static class WindowFinder
{
    /// <summary>Visible top-level windows of other processes, front to back.</summary>
    public static List<WindowInfo> GetWindows()
    {
        var list = new List<WindowInfo>();
        uint self = (uint)Environment.ProcessId;
        var desktop = ScreenCapture.VirtualScreen;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) return true;

            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hwnd, out r);
            var bounds = RectUtil.Intersect(RectUtil.From(r), desktop);
            if (bounds.Width < 4 || bounds.Height < 4) return true;

            list.Add(new WindowInfo { Handle = hwnd, Bounds = bounds, Title = GetTitle(hwnd), ProcessId = pid });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string? GetForegroundProcessName()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId ? null : GetProcessName(pid);
    }

    public static string? GetProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}

public static class RectUtil
{
    internal static Int32Rect From(RECT r) => new(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));

    public static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width), y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return x2 > x1 && y2 > y1 ? new Int32Rect(x1, y1, x2 - x1, y2 - y1) : new Int32Rect(x1, y1, 0, 0);
    }

    public static bool Contains(Int32Rect r, Point p) =>
        p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.Width && p.Y < r.Y + r.Height;

    public static Int32Rect FromPoints(Point a, Point b)
    {
        int x1 = (int)Math.Floor(Math.Min(a.X, b.X)), y1 = (int)Math.Floor(Math.Min(a.Y, b.Y));
        int x2 = (int)Math.Ceiling(Math.Max(a.X, b.X)), y2 = (int)Math.Ceiling(Math.Max(a.Y, b.Y));
        return new Int32Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }
}
