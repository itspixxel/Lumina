using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumina.Services;

/// <summary>Runs WPF imaging work (encoders, RenderTargetBitmap) on a background STA thread.</summary>
public static class StaTask
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "Lumina.Imaging" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static Task Run(Action work) => Run(() => { work(); return true; });
}
