using System;
using System.Windows;
using Lumina.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace Lumina.Recording;

/// <summary>
/// Grabs screen pixels with DXGI Desktop Duplication: the GPU hands over each new desktop image, so frames keep up
/// with the monitor's refresh rate (GDI tops out around 50 a second at 1080p). Covers one monitor; recordings that
/// span monitors, or run where duplication isn't available (Remote Desktop, the secure desktop), use GDI instead.
/// Coordinates are physical virtual-screen pixels, the same as GDI's.
/// </summary>
internal sealed class DesktopDuplication : IDisposable
{
    private readonly IDXGIAdapter1 _adapter;
    private readonly IDXGIOutput1 _output;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    /// <summary>The monitor's area on the virtual screen.</summary>
    private readonly Int32Rect _bounds;
    private IDXGIOutputDuplication? _duplication;
    /// <summary>The whole desktop as of the last new frame, kept on the GPU so a still screen (no new frame) or a
    /// moved recording frame can be copied again.</summary>
    private ID3D11Texture2D? _desktop;
    /// <summary>The recorded part, copied where the CPU can read it.</summary>
    private ID3D11Texture2D? _staging;
    private bool _hasImage;
    private long _retryAt;

    /// <summary>The monitor's refresh rate in frames a second; 0 when Windows doesn't say.</summary>
    public double RefreshRate { get; private set; }

    private DesktopDuplication(IDXGIAdapter1 adapter, IDXGIOutput1 output, Int32Rect bounds)
    {
        _adapter = adapter;
        _output = output;
        _bounds = bounds;
        D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out _device!, out _context!).CheckError();
    }

    /// <summary>A grabber for the monitor that holds all of <paramref name="area"/>; null when there's none.</summary>
    public static DesktopDuplication? TryCreate(Int32Rect area)
    {
        try
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    var desc = output.Description;
                    var r = desc.DesktopCoordinates;
                    var bounds = new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                    bool fits = desc.AttachedToDesktop && Contains(bounds, area);
                    // Rotated monitors hand over rotated images; leave those to GDI.
                    if (!fits || desc.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                    {
                        output.Dispose();
                        continue;
                    }
                    var output1 = output.QueryInterface<IDXGIOutput1>();
                    output.Dispose();
                    var grabber = new DesktopDuplication(adapter, output1, bounds);
                    if (grabber.Open()) return grabber;
                    grabber.Dispose();
                    return null;
                }
                adapter.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Starting Desktop Duplication");
        }
        return null;
    }

    private static bool Contains(Int32Rect outer, Int32Rect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    /// <summary>Whether <paramref name="source"/> is on this grabber's monitor (a moved recording frame may not be).</summary>
    public bool Covers(Int32Rect source) => Contains(_bounds, source);

    private bool Open()
    {
        try
        {
            _duplication = _output.DuplicateOutput(_device);
            var mode = _duplication.Description.ModeDescription;
            RefreshRate = mode.RefreshRate.Denominator > 0 ? (double)mode.RefreshRate.Numerator / mode.RefreshRate.Denominator : 0;
            _hasImage = false;
            return true;
        }
        catch (Exception ex)
        {
            // Not available here (e.g. Remote Desktop), or the secure desktop is showing.
            Log.Info($"Desktop Duplication unavailable: {ex.Message}");
            _duplication = null;
            return false;
        }
    }

    /// <summary>
    /// Copies the screen at <paramref name="source"/> (as of its latest image) into a top-down BGRA buffer with rows
    /// <paramref name="stride"/> bytes apart. False when there's no image to give (duplication was lost and hasn't
    /// come back yet); the caller grabs that frame another way.
    /// </summary>
    public unsafe bool TryGrab(Int32Rect source, IntPtr destination, int stride)
    {
        if (_duplication == null)
        {
            // Lost (display mode change, secure desktop, full-screen switch): try again now and then.
            if (Environment.TickCount64 < _retryAt) return false;
            _retryAt = Environment.TickCount64 + 500;
            if (!Open()) return false;
        }

        // Take the newest desktop image if there is one; otherwise the last one still stands.
        var result = _duplication!.AcquireNextFrame(_hasImage ? 0u : 100u, out OutduplFrameInfo info, out IDXGIResource? resource);
        if (result.Success)
        {
            try
            {
                if (info.LastPresentTime != 0 || !_hasImage)
                {
                    using var frame = resource!.QueryInterface<ID3D11Texture2D>();
                    if (_desktop == null)
                    {
                        var d = frame.Description;
                        _desktop = _device.CreateTexture2D(new Texture2DDescription
                        {
                            Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format,
                            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                        });
                    }
                    _context.CopyResource(_desktop, frame);
                    _hasImage = true;
                }
            }
            finally
            {
                resource?.Dispose();
                _duplication.ReleaseFrame();
            }
        }
        else if (result.Code != Vortice.DXGI.ResultCode.WaitTimeout.Code)
        {
            Log.Info($"Desktop Duplication lost (0x{result.Code:X8}); reopening");
            Close();
            return false;
        }
        if (!_hasImage || _desktop == null) return false;

        int w = source.Width, h = source.Height;
        if (_staging == null || _staging.Description.Width != (uint)w || _staging.Description.Height != (uint)h)
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
            });
        }
        int x = source.X - _bounds.X, y = source.Y - _bounds.Y;
        var box = new Vortice.Mathematics.Box(x, y, 0, x + w, y + h, 1);
        _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, _desktop, 0, box);

        var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* src = (byte*)map.DataPointer, dst = (byte*)destination;
            int rowBytes = w * 4;
            for (int row = 0; row < h; row++)
                Buffer.MemoryCopy(src + row * map.RowPitch, dst + row * stride, rowBytes, rowBytes);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
        return true;
    }

    private void Close()
    {
        _duplication?.Dispose();
        _duplication = null;
        _desktop?.Dispose();
        _desktop = null;
        _hasImage = false;
        _retryAt = Environment.TickCount64 + 500;
    }

    public void Dispose()
    {
        Close();
        _staging?.Dispose();
        _context.Dispose();
        _device.Dispose();
        _output.Dispose();
        _adapter.Dispose();
    }
}
