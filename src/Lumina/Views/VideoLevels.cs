using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Lumina.Recording;
using Lumina.Services;

namespace Lumina.Views;

/// <summary>
/// WPF plays video through the old Windows Media pipeline, which on many PCs (NVIDIA's default "limited" video
/// range, for one) hands back pixels squeezed into 16–235, so the preview looks grey and dull next to the file.
/// It depends on the driver, so instead of guessing, this plays a tiny black/grey/white clip through the same
/// pipeline once, sees where black and white land, and builds an effect that stretches them back.
/// </summary>
public static class VideoLevels
{
    private static Task<Effect?>? _effect;

    /// <summary>An effect that undoes this PC's squeeze, or null when there's none (or it can't be measured).</summary>
    public static Task<Effect?> CorrectionAsync() => _effect ??= MeasureAsync();

    private static async Task<Effect?> MeasureAsync()
    {
        try
        {
            if (!Ffmpeg.IsAvailable) return null;
            // Studio-range black, grey and white bars, tagged like Lumina's own recordings.
            string clip = Path.Combine(AppPaths.TempDir, "levels.mp4");
            await Ffmpeg.RunJobAsync(
                "-f lavfi -i color=black:s=96x64:d=1:r=10,format=yuv420p " +
                "-vf \"geq=lum='if(lt(X,32),16,if(lt(X,64),126,235))':cb=128:cr=128," +
                "setparams=range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709\" " +
                $"-c:v libx264 -qp 1 {Ffmpeg.Quote(clip)}", 0, null);

            var bars = await GrabAsync(clip);
            if (bars == null)
            {
                Log.Info("Video levels: no frame to measure");
                return null;
            }
            var (black, white) = bars.Value;
            Log.Info($"Video levels: black {black}, white {white}");
            // A healthy pipeline gives ~0 and ~255; only correct a clear squeeze.
            if (black < 6 && white > 249 || white - black < 128) return null;
            return new LevelsEffect(black / 255.0, white / 255.0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Measuring video levels");
            return null;
        }
    }

    /// <summary>Plays the clip and reads back the black and white bars as the pipeline renders them.</summary>
    private static async Task<(int Black, int White)?> GrabAsync(string clip)
    {
        var player = new MediaPlayer { IsMuted = true };
        try
        {
            var opened = new TaskCompletionSource<bool>();
            player.MediaOpened += (_, _) => opened.TrySetResult(true);
            player.MediaFailed += (_, e) => opened.TrySetException(e.ErrorException);
            player.Open(new Uri(clip));
            player.Play(); // MediaOpened only fires once playback is requested
            if (await Task.WhenAny(opened.Task, Task.Delay(5000)) != opened.Task) return null;
            await opened.Task;
            player.Pause();
            player.Position = TimeSpan.FromMilliseconds(500);

            var pixels = new byte[96 * 64 * 4];
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(50);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) dc.DrawVideo(player, new Rect(0, 0, 96, 64));
                var bitmap = new RenderTargetBitmap(96, 64, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.CopyPixels(pixels, 96 * 4, 0);
                // Green channel, middle row, centre of each bar. The grey bar shows once a frame has arrived.
                int Bar(int x) => pixels[(32 * 96 + x) * 4 + 1];
                if (Bar(48) > 40) return (Bar(16), Bar(80));
            }
            return null;
        }
        finally
        {
            player.Close();
        }
    }

    /// <summary>Maps <c>black..white</c> back to 0..1. Works on premultiplied colour, so letterboxing stays clear.</summary>
    private sealed class LevelsEffect : ShaderEffect
    {
        private const string Source = @"
sampler2D input : register(s0);
float2 levels : register(c0);
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 c = tex2D(input, uv);
    c.rgb = min(saturate((c.rgb - levels.x * c.a) / (levels.y - levels.x)), c.a);
    return c;
}";

        private static PixelShader? _shader;

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty(nameof(Input), typeof(LevelsEffect), 0);

        public static readonly DependencyProperty LevelsProperty =
            DependencyProperty.Register(nameof(Levels), typeof(Point), typeof(LevelsEffect),
                new UIPropertyMetadata(new Point(0, 1), PixelShaderConstantCallback(0)));

        public LevelsEffect(double black, double white)
        {
            PixelShader = _shader ??= Compile();
            Levels = new Point(black, white);
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(LevelsProperty);
        }

        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        public Point Levels
        {
            get => (Point)GetValue(LevelsProperty);
            set => SetValue(LevelsProperty, value);
        }

        /// <summary>Compiles the shader with the D3D compiler that ships with Windows, so there's no build step.</summary>
        private static PixelShader Compile()
        {
            int hr = D3DCompile(Source, (IntPtr)Source.Length, null, IntPtr.Zero, IntPtr.Zero, "main", "ps_2_0", 0, 0,
                out IntPtr code, out IntPtr errors);
            try
            {
                if (hr < 0) throw new InvalidOperationException("Shader didn't compile: " + BlobText(errors));
                var bytes = new byte[(long)BlobSize(code)];
                Marshal.Copy(BlobPointer(code), bytes, 0, bytes.Length);
                var shader = new PixelShader();
                shader.SetStreamSource(new MemoryStream(bytes));
                return shader;
            }
            finally
            {
                if (code != IntPtr.Zero) Marshal.Release(code);
                if (errors != IntPtr.Zero) Marshal.Release(errors);
            }
        }

        // ID3DBlob: IUnknown (3 slots), then GetBufferPointer, GetBufferSize.
        private static unsafe IntPtr BlobPointer(IntPtr blob) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)(*(IntPtr**)blob)[3])(blob);

        private static unsafe nuint BlobSize(IntPtr blob) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)(*(IntPtr**)blob)[4])(blob);

        private static string BlobText(IntPtr blob) =>
            blob == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(BlobPointer(blob), (int)BlobSize(blob)) ?? "";

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        private static extern int D3DCompile(string source, IntPtr sourceSize, string? sourceName, IntPtr defines,
            IntPtr include, string entryPoint, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
    }
}
