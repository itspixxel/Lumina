using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Lumina.Capture;
using Lumina.Recording;
using Lumina.Services;
using Lumina.Views;

namespace Lumina;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateSignal;
    private TrayIcon? _tray;
    private KeyboardHook? _hook;

    public static KeyboardHook? Hook => Instance._hook;
    internal static TrayIcon? Tray => Instance._tray;

    public static App Instance => (App)Current;
    public static AppSettings Settings { get; private set; } = new();
    public CaptureController Controller { get; } = new();
    public RecordingController Recorder { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool updated = e.Args.Contains("--updated");
        _singleInstance = new Mutex(true, @"Local\Lumina.SingleInstance", out bool isFirst);
        if (!isFirst && updated) isFirst = WaitForPreviousInstance(_singleInstance);
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Lumina.Activate");
        if (!isFirst)
        {
            // Already running: bring the existing instance forward instead.
            _activateSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (_activateSignal.WaitOne()) Dispatcher.BeginInvoke(ShowHome);
        })
        { IsBackground = true, Name = "Lumina.Activation" }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            LuminaDialog.Alert(null, "Something went wrong", args.Exception.Message, DialogKind.Warning);
            args.Handled = true;
        };

        Settings = SettingsService.Load();

        _tray = new TrayIcon();
        _hook = new KeyboardHook(Dispatcher);
        _hook.CaptureRequested += mode =>
        {
            if (mode == CaptureMode.Record) Recorder.Toggle();
            else if (!Recorder.IsRecording) Controller.Start(mode);
        };
        ApplyShortcuts();
        _hook.Start();

        if (!e.Args.Contains("--background")) ShowHome();
        if (!Settings.SetupShown)
        {
            // First launch: say hello and offer the FFmpeg download, so recording is ready before it's needed.
            if (Ffmpeg.IsAvailable)
            {
                Settings.SetupShown = true;
                SettingsService.Save(Settings);
            }
            Dispatcher.BeginInvoke(() => RecordingSetupWindow.ShowSetup(firstRun: true), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        else if (Ffmpeg.IsAvailable && Settings.HardwareEncoding)
        {
            _ = Ffmpeg.PickEncoderAsync(); // warm up so the first recording starts instantly
        }
        Updater.Start(updated);
        ThumbnailCache.PruneInBackground();
        TextIndexer.Start();
    }

    /// <summary>After an update the old version may still be shutting down; take over once it has.</summary>
    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
            return true; // it exited without releasing; ours now
        }
    }

    public static void ApplyShortcuts()
    {
        if (Instance._hook is { } hook)
        {
            hook.SetBindings(Settings.Hotkeys!);
        }
        Instance._tray?.UpdateTooltip();
    }

    public static void ShowHome()
    {
        var home = Current.Windows.OfType<HomeWindow>().FirstOrDefault() ?? new HomeWindow();
        home.Show();
        if (home.WindowState == WindowState.Minimized) home.WindowState = WindowState.Normal;
        home.Activate();
    }

    public static void ShowSettings()
    {
        var settings = Current.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? new SettingsWindow();
        settings.Show();
        if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
        settings.Activate();
    }

    public static void Quit() => Current.Shutdown();

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
