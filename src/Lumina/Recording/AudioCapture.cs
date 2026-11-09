using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Lumina.Services;

namespace Lumina.Recording;

public enum AudioFlow { Output = 0, Input = 1 }

public sealed record AudioDevice(string Id, string Name);

/// <summary>Lists playback and recording devices through the Windows audio (WASAPI) endpoint API.</summary>
public static class AudioDevices
{
    public static List<AudioDevice> List(AudioFlow flow)
    {
        var list = new List<AudioDevice>();
        IntPtr enumerator = IntPtr.Zero, collection = IntPtr.Zero;
        try
        {
            enumerator = Wasapi.CreateEnumerator();
            Marshal.ThrowExceptionForHR(Wasapi.EnumAudioEndpoints(enumerator, (int)flow, Wasapi.DEVICE_STATE_ACTIVE, out collection));
            Marshal.ThrowExceptionForHR(Wasapi.GetCount(collection, out int count));
            for (int i = 0; i < count; i++)
            {
                if (Wasapi.Item(collection, i, out IntPtr device) != 0) continue;
                try
                {
                    if (Wasapi.GetId(device) is { } id) list.Add(new AudioDevice(id, Wasapi.FriendlyName(device) ?? "Audio device"));
                }
                finally
                {
                    Wasapi.Release(device);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Listing audio devices");
        }
        finally
        {
            Wasapi.Release(collection);
            Wasapi.Release(enumerator);
        }
        return list;
    }

    /// <summary>The device Windows currently uses for this flow, or null when there's none.</summary>
    public static AudioDevice? Default(AudioFlow flow)
    {
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero;
        try
        {
            enumerator = Wasapi.CreateEnumerator();
            if (Wasapi.GetDefaultAudioEndpoint(enumerator, (int)flow, Wasapi.eConsole, out device) != 0) return null;
            return Wasapi.GetId(device) is { } id ? new AudioDevice(id, Wasapi.FriendlyName(device) ?? "Audio device") : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Finding the default audio device");
            return null;
        }
        finally
        {
            Wasapi.Release(device);
            Wasapi.Release(enumerator);
        }
    }

    /// <summary>"Default output (Speakers)": the label for following whatever Windows uses.</summary>
    public static string DefaultLabel(AudioFlow flow)
    {
        string kind = flow == AudioFlow.Output ? "Default output" : "Default microphone";
        return Default(flow) is { } d ? $"{kind} ({d.Name})" : kind;
    }
}

/// <summary>
/// Records one audio device to a WAV file in the device's own format. Output devices are recorded with
/// WASAPI loopback (what's playing); input devices are microphones.
/// <para>
/// The file follows the recording clock rather than the device: packets are dropped while paused, silence
/// fills the gaps (loopback delivers nothing while nothing plays), and muting writes silence. So the audio
/// always lines up with the video, however often the user pauses.
/// </para>
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    private readonly AudioFlow _flow;
    private readonly string? _deviceId;
    private readonly RecordingClock _clock;
    private readonly string _path;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly ManualResetEventSlim _ready = new();
    private Exception? _startError;

    private FileStream? _file;
    private byte[] _format = Array.Empty<byte>();
    private int _blockAlign, _sampleRate;
    private long _written; // frames

    public volatile bool Muted;
    public string Path => _path;
    public bool HasAudio => _written > 0;

    public AudioCapture(AudioFlow flow, string? deviceId, RecordingClock clock, string path)
    {
        _flow = flow;
        _deviceId = deviceId;
        _clock = clock;
        _path = path;
    }

    /// <summary>Opens the device and starts listening. Throws if the device can't be opened.</summary>
    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = $"Lumina.Audio.{_flow}", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _ready.Wait();
        if (_startError != null) throw _startError;
    }

    private void Run()
    {
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero, client = IntPtr.Zero, capture = IntPtr.Zero;
        try
        {
            enumerator = Wasapi.CreateEnumerator();
            int hr = _deviceId != null
                ? Wasapi.GetDevice(enumerator, _deviceId, out device)
                : Wasapi.GetDefaultAudioEndpoint(enumerator, (int)_flow, Wasapi.eConsole, out device);
            // A remembered device that's been unplugged falls back to the default.
            if (hr != 0 && _deviceId != null) hr = Wasapi.GetDefaultAudioEndpoint(enumerator, (int)_flow, Wasapi.eConsole, out device);
            Marshal.ThrowExceptionForHR(hr);

            Marshal.ThrowExceptionForHR(Wasapi.Activate(device, Wasapi.IID_IAudioClient, out client));
            Marshal.ThrowExceptionForHR(Wasapi.GetMixFormat(client, out IntPtr mix));
            try
            {
                int cbSize = Marshal.ReadInt16(mix, 16);
                _format = new byte[18 + cbSize];
                Marshal.Copy(mix, _format, 0, _format.Length);
                _sampleRate = BitConverter.ToInt32(_format, 4);
                _blockAlign = BitConverter.ToInt16(_format, 12);
                int flags = _flow == AudioFlow.Output ? Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK : 0;
                Marshal.ThrowExceptionForHR(Wasapi.Initialize(client, flags, 2_000_000, mix)); // 200 ms buffer
            }
            finally
            {
                Marshal.FreeCoTaskMem(mix);
            }
            Marshal.ThrowExceptionForHR(Wasapi.GetService(client, Wasapi.IID_IAudioCaptureClient, out capture));

            _file = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            WriteHeader();
            Marshal.ThrowExceptionForHR(Wasapi.Start(client));
        }
        catch (Exception ex)
        {
            _startError = ex;
            _file?.Dispose();
            _file = null;
            Release(capture, client, device, enumerator);
            _ready.Set();
            return;
        }
        _ready.Set();

        var silence = new byte[_blockAlign * _sampleRate / 10];
        var copy = Array.Empty<byte>();
        FillGap(silence); // opened mid-recording: start the track at the current time
        try
        {
            while (!_stop)
            {
                while (Wasapi.GetNextPacketSize(capture, out uint packet) == 0 && packet > 0)
                {
                    if (Wasapi.GetBuffer(capture, out IntPtr data, out uint frames, out uint bufferFlags) != 0) break;
                    if (_written == 0) FillGap(silence); // opened while paused: catch up once the clock runs
                    if (Accept(frames))
                    {
                        int bytes = (int)frames * _blockAlign;
                        if (Muted || (bufferFlags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                        {
                            WriteSilence(frames, silence);
                        }
                        else
                        {
                            if (copy.Length < bytes) copy = new byte[bytes];
                            Marshal.Copy(data, copy, 0, bytes);
                            _file!.Write(copy, 0, bytes);
                            _written += frames;
                        }
                    }
                    Wasapi.ReleaseBuffer(capture, frames);
                }
                FillGap(silence);
                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Audio capture ({_flow})");
        }
        finally
        {
            Wasapi.Stop(client);
            Release(capture, client, device, enumerator);
            FinishFile();
        }
    }

    private static void Release(params IntPtr[] objects)
    {
        foreach (var o in objects) Wasapi.Release(o);
    }

    private long Expected => (long)(_clock.Elapsed.TotalSeconds * _sampleRate);

    /// <summary>Whether a packet belongs in the file: not while paused, and not when we're already ahead of the clock.</summary>
    private bool Accept(uint frames) =>
        _clock.IsRunning && _written + frames <= Expected + _sampleRate / 6;

    /// <summary>Pads silence when the device has gone quiet, so the track keeps pace with the video.</summary>
    private void FillGap(byte[] silence)
    {
        if (!_clock.IsRunning) return;
        long behind = Expected - _written - _sampleRate / 50; // allow 20 ms of device latency
        if (behind > _sampleRate / 20) WriteSilence(behind, silence);
    }

    private void WriteSilence(long frames, byte[] silence)
    {
        long bytes = frames * _blockAlign;
        while (bytes > 0)
        {
            int n = (int)Math.Min(bytes, silence.Length);
            _file!.Write(silence, 0, n);
            bytes -= n;
        }
        _written += frames;
    }

    private void WriteHeader()
    {
        var w = new BinaryWriter(_file!);
        w.Write("RIFF"u8);
        w.Write(0); // patched in FinishFile
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(_format.Length);
        w.Write(_format);
        w.Write("data"u8);
        w.Write(0);
        w.Flush();
    }

    private void FinishFile()
    {
        if (_file == null) return;
        try
        {
            long dataBytes = _written * _blockAlign;
            int dataStart = 12 + 8 + _format.Length + 8;
            _file.Position = 4;
            _file.Write(BitConverter.GetBytes((int)Math.Min(int.MaxValue, dataStart - 8 + dataBytes)));
            _file.Position = dataStart - 4;
            _file.Write(BitConverter.GetBytes((int)Math.Min(int.MaxValue, dataBytes)));
        }
        finally
        {
            _file.Dispose();
            _file = null;
        }
    }

    /// <summary>Stops listening and closes the WAV file.</summary>
    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
    }

    public void Dispose() => Stop();
}

/// <summary>
/// The slice of the Core Audio COM API that recording needs, called straight through each interface's
/// vtable. (The runtime's COM wrappers fail to bind these objects on some Windows builds.)
/// </summary>
internal static unsafe class Wasapi
{
    public const int DEVICE_STATE_ACTIVE = 1, eConsole = 0, CLSCTX_ALL = 23;
    public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid PKEY_Device_FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, int context, in Guid iid, out IntPtr obj);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(void* pv);

    private static void** Vtbl(IntPtr obj) => *(void***)obj;

    public static IntPtr CreateEnumerator()
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL, IID_IMMDeviceEnumerator, out IntPtr e));
        return e;
    }

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(obj)[2])(obj);
    }

    // ----- IMMDeviceEnumerator -----

    public static int EnumAudioEndpoints(IntPtr e, int flow, int state, out IntPtr collection)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Vtbl(e)[3])(e, flow, state, &result);
        collection = result;
        return hr;
    }

    public static int GetDefaultAudioEndpoint(IntPtr e, int flow, int role, out IntPtr device)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Vtbl(e)[4])(e, flow, role, &result);
        device = result;
        return hr;
    }

    public static int GetDevice(IntPtr e, string id, out IntPtr device)
    {
        IntPtr result;
        int hr;
        fixed (char* pid = id) hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Vtbl(e)[5])(e, pid, &result);
        device = result;
        return hr;
    }

    // ----- IMMDeviceCollection -----

    public static int GetCount(IntPtr c, out int count)
    {
        int n;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(c)[3])(c, &n);
        count = n;
        return hr;
    }

    public static int Item(IntPtr c, int index, out IntPtr device)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Vtbl(c)[4])(c, index, &result);
        device = result;
        return hr;
    }

    // ----- IMMDevice -----

    public static int Activate(IntPtr device, Guid iid, out IntPtr obj)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, IntPtr, IntPtr*, int>)Vtbl(device)[3])(device, &iid, CLSCTX_ALL, IntPtr.Zero, &result);
        obj = result;
        return hr;
    }

    public static string? GetId(IntPtr device)
    {
        char* id;
        if (((delegate* unmanaged[Stdcall]<IntPtr, char**, int>)Vtbl(device)[5])(device, &id) != 0) return null;
        try
        {
            return new string(id);
        }
        finally
        {
            Marshal.FreeCoTaskMem((IntPtr)id);
        }
    }

    public static string? FriendlyName(IntPtr device)
    {
        IntPtr store;
        // OpenPropertyStore(STGM_READ)
        if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Vtbl(device)[4])(device, 0, &store) != 0) return null;
        try
        {
            byte* key = stackalloc byte[20]; // PROPERTYKEY { GUID fmtid; DWORD pid; }
            *(Guid*)key = PKEY_Device_FriendlyName;
            *(int*)(key + 16) = 14;
            byte* value = stackalloc byte[24]; // PROPVARIANT
            new Span<byte>(value, 24).Clear();
            // IPropertyStore::GetValue
            if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, int>)Vtbl(store)[5])(store, key, value) != 0) return null;
            try
            {
                return *(ushort*)value == 31 ? new string(*(char**)(value + 8)) : null; // VT_LPWSTR
            }
            finally
            {
                PropVariantClear(value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    // ----- IAudioClient -----

    public static int Initialize(IntPtr client, int flags, long bufferDuration, IntPtr format) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, IntPtr, IntPtr, int>)Vtbl(client)[3])(
            client, 0 /* shared */, flags, bufferDuration, 0, format, IntPtr.Zero);

    public static int GetMixFormat(IntPtr client, out IntPtr format)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(client)[8])(client, &result);
        format = result;
        return hr;
    }

    public static int Start(IntPtr client) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(client)[10])(client);

    public static int Stop(IntPtr client) =>
        client == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(client)[11])(client);

    public static int GetService(IntPtr client, Guid iid, out IntPtr service)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(client)[14])(client, &iid, &result);
        service = result;
        return hr;
    }

    // ----- IAudioCaptureClient -----

    public static int GetBuffer(IntPtr capture, out IntPtr data, out uint frames, out uint flags)
    {
        IntPtr d;
        uint f, fl;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, ulong*, ulong*, int>)Vtbl(capture)[3])(capture, &d, &f, &fl, null, null);
        data = d;
        frames = f;
        flags = fl;
        return hr;
    }

    public static int ReleaseBuffer(IntPtr capture, uint frames) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Vtbl(capture)[4])(capture, frames);

    public static int GetNextPacketSize(IntPtr capture, out uint frames)
    {
        uint f;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Vtbl(capture)[5])(capture, &f);
        frames = f;
        return hr;
    }
}
