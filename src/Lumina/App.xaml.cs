using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Lumina.Capture;
using Lumina.Services;
using Lumina.Views;

namespace Lumina;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateSignal;
    private TrayIcon? _tray;
    private KeyboardHook? _hook;

    public static App Instance => (App)Current;
    public static AppSettings Settings { get; private set; } = new();
    public CaptureController Controller { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\Lumina.SingleInstance", out bool isFirst);
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
        _hook.CaptureRequested += () => Controller.Start(CaptureMode.Region);
        ApplyShortcuts();
        _hook.Start();

        if (!e.Args.Contains("--background")) ShowHome();
    }

    public static void ApplyShortcuts()
    {
        if (Instance._hook is { } hook)
        {
            hook.InterceptWinShiftS = Settings.HookWinShiftS;
            hook.InterceptPrintScreen = Settings.HookPrintScreen;
        }
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
