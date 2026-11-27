using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Lumina.Capture;
using Lumina.Native;
using Microsoft.Win32.SafeHandles;

namespace Lumina.Services;

/// <summary>
/// Lets scripts drive the running Lumina from the command line, e.g.
/// <c>Lumina.exe --record --window "Notepad" --seconds 5 --no-editor</c>. A second Lumina.exe forwards its
/// arguments over a named pipe that only the current user can open from this machine, waits for the
/// running instance to answer, prints the saved file's path and exits.
/// </summary>
public static class CommandChannel
{
    /// <summary>Pipe names are machine-wide, so the session id keeps each signed-in session's Lumina apart.</summary>
    private static string PipeName => $"Lumina.Command.{Process.GetCurrentProcess().SessionId}";

    /// <summary>Where the last recording made from the command line is noted, for callers that can't read stdout.</summary>
    public static string LastRecordingFile => Path.Combine(AppPaths.DataDir, "last-recording.txt");

    public static bool IsCommand(string[] args) => args.Any(a => a is "--record" or "--stop");

    // ---- Running instance ---------------------------------------------------------------------------

    /// <summary>Starts listening for commands; each one runs on the UI thread through <paramref name="handler"/>.</summary>
    public static void StartServer(Func<string[], Task<string>> handler)
    {
        _ = Task.Run(() => ListenAsync(handler));
    }

    private static async Task ListenAsync(Func<string[], Task<string>> handler)
    {
        while (true)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Command pipe");
                return; // another Lumina already owns it
            }
            try
            {
                await pipe.WaitForConnectionAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Command pipe connection");
                pipe.Dispose();
                await Task.Delay(1000);
                continue;
            }
            _ = Task.Run(() => ServeAsync(pipe, handler));
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        // Only this user, and never over the network.
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, Func<string[], Task<string>> handler)
    {
        using (pipe)
        {
            Reply reply;
            try
            {
                var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                string line = await reader.ReadLineAsync() ?? "";
                var args = JsonSerializer.Deserialize<string[]>(line) ?? Array.Empty<string>();
                Log.Info("Command: " + string.Join(" ", args));
                string path = await Application.Current.Dispatcher.InvokeAsync(() => handler(args)).Task.Unwrap();
                try
                {
                    File.WriteAllText(LastRecordingFile, path + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Note last recording");
                }
                reply = new Reply(true, path, null);
            }
            catch (Exception ex)
            {
                if (ex is not CommandException) Log.Error(ex, "Command");
                reply = new Reply(false, null, ex.Message);
            }
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(reply) + "\n");
                await pipe.WriteAsync(bytes);
                await pipe.FlushAsync();
            }
            catch (IOException)
            {
                // The caller gave up waiting; the recording is saved either way.
            }
        }
    }

    private sealed record Reply(bool Ok, string? Path, string? Error);

    // ---- Command line client ------------------------------------------------------------------------

    /// <summary>
    /// Sends the command to the running Lumina (starting one in the background first when
    /// <paramref name="startIfNeeded"/>), waits for the answer and prints it. Returns the exit code.
    /// </summary>
    public static int RunClient(string[] args, bool startIfNeeded)
    {
        var output = new ConsoleOutput();
        try
        {
            if (startIfNeeded)
            {
                if (args.Contains("--stop")) return output.Fail("Lumina isn't running, so there is no recording to stop.", 2);
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--background") { UseShellExecute = false });
            }

            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            try
            {
                pipe.Connect(startIfNeeded ? 30_000 : 10_000);
            }
            catch (TimeoutException)
            {
                return output.Fail(startIfNeeded
                    ? "Lumina didn't start in time."
                    : "The running Lumina doesn't accept commands (it may be an older version). Quit it and try again.", 2);
            }

            byte[] request = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(args) + "\n");
            pipe.Write(request);
            pipe.Flush();

            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            string? line = reader.ReadLine();
            var reply = line == null ? null : JsonSerializer.Deserialize<Reply>(line);
            if (reply == null) return output.Fail("Lumina closed before the recording finished.", 1);
            if (!reply.Ok) return output.Fail(reply.Error ?? "Recording failed.", 1);
            output.Out(reply.Path!);
            return 0;
        }
        catch (Exception ex)
        {
            return output.Fail(ex.Message, 1);
        }
    }

    /// <summary>
    /// Stdout and stderr for a GUI app: the caller's redirected handles when it captures output (PowerShell
    /// assignments, pipes, bash), otherwise the console of the shell that started us, if any.
    /// </summary>
    private sealed class ConsoleOutput
    {
        private bool _attached;

        public void Out(string text) => Write(NativeMethods.STD_OUTPUT_HANDLE, text);

        public int Fail(string message, int code)
        {
            Write(NativeMethods.STD_ERROR_HANDLE, "Lumina: " + message);
            return code;
        }

        private void Write(int which, string text)
        {
            try
            {
                IntPtr handle = NativeMethods.GetStdHandle(which);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1) || NativeMethods.GetFileType(handle) == NativeMethods.FILE_TYPE_UNKNOWN)
                {
                    if (!_attached) _attached = NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
                    if (!_attached) return;
                    handle = NativeMethods.CreateFile("CONOUT$", NativeMethods.GENERIC_WRITE, NativeMethods.FILE_SHARE_WRITE,
                        IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                    if (handle == new IntPtr(-1)) return;
                    using var console = new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Write, 1);
                    WriteLine(console, text);
                    return;
                }
                using var stream = new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write, 1);
                WriteLine(stream, text);
            }
            catch
            {
                // Nowhere to print; the exit code and last-recording.txt still say how it went.
            }
        }

        private static void WriteLine(Stream stream, string text)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text + Environment.NewLine);
            stream.Write(bytes);
            stream.Flush();
        }
    }
}

/// <summary>A command line mistake or refusal, reported to the caller as is.</summary>
public sealed class CommandException(string message) : Exception(message);

/// <summary>What to record and how, parsed from <c>--record</c> arguments.</summary>
public sealed class RecordCommand
{
    public string? WindowTitle { get; private set; }
    public Int32Rect? Region { get; private set; }
    /// <summary>0 for the primary screen, otherwise 1-based from the left.</summary>
    public int? Screen { get; private set; }
    public double? Seconds { get; private set; }
    public bool Countdown { get; private set; } = true;
    public bool OpenEditor { get; private set; } = true;
    public bool SystemAudio { get; private set; } = true;
    public bool Microphone { get; private set; }

    /// <summary>Parses the arguments of a command; returns null for <c>--stop</c>.</summary>
    public static RecordCommand? Parse(string[] args)
    {
        bool record = false, stop = false;
        var cmd = new RecordCommand();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new CommandException($"{a} needs a value.");
            switch (a)
            {
                case "--record": record = true; break;
                case "--stop": stop = true; break;
                case "--window":
                    cmd.WindowTitle = Next();
                    if (cmd.WindowTitle.Length == 0) throw new CommandException("--window needs part of a window title.");
                    break;
                case "--region": cmd.Region = ParseRegion(Next()); break;
                case "--screen":
                    cmd.Screen = 0;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                    {
                        if (n < 1) throw new CommandException("--screen counts from 1.");
                        cmd.Screen = n;
                        i++;
                    }
                    break;
                case "--seconds":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) || s <= 0 || s > 24 * 3600)
                        throw new CommandException("--seconds needs a positive number.");
                    cmd.Seconds = s;
                    break;
                case "--no-countdown": cmd.Countdown = false; break;
                case "--no-editor": cmd.OpenEditor = false; break;
                case "--no-audio": cmd.SystemAudio = false; cmd.Microphone = false; break;
                case "--mic": cmd.Microphone = true; break;
                default: throw new CommandException($"Unknown option {a}.");
            }
        }
        if (record == stop) throw new CommandException("Use either --record or --stop.");
        if (stop) return null;
        int targets = (cmd.WindowTitle != null ? 1 : 0) + (cmd.Region != null ? 1 : 0) + (cmd.Screen != null ? 1 : 0);
        if (targets != 1) throw new CommandException("Say what to record with one of --window \"title\", --region x,y,w,h or --screen.");
        return cmd;
    }

    private static Int32Rect ParseRegion(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var n = new int[4];
        if (parts.Length != 4 || !parts.Select((p, k) => int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n[k])).All(ok => ok)
            || n[2] < 2 || n[3] < 2)
            throw new CommandException("--region needs x,y,width,height in screen pixels, e.g. 100,100,1280,720.");
        return new Int32Rect(n[0], n[1], n[2], n[3]);
    }

    /// <summary>Finds the area to record, bringing a window to the front first.</summary>
    public async Task<RecordArea> ResolveAsync()
    {
        if (WindowTitle != null)
        {
            var window = WindowFinder.FindByTitle(WindowTitle)
                ?? throw new CommandException($"No window with \"{WindowTitle}\" in its title.");
            if (!WindowFinder.BringToFront(window.Handle))
                Log.Info($"Couldn't bring \"{window.Title}\" to the front; recording its area anyway");
            await Task.Delay(300); // let a restore or activation animation settle
            var bounds = WindowFinder.GetBounds(window.Handle);
            if (bounds.Width < 2 || bounds.Height < 2) throw new CommandException($"\"{window.Title}\" isn't on screen.");
            return new RecordArea(bounds, window.ProcessName);
        }
        if (Region is { } region)
        {
            var bounds = RectUtil.Intersect(region, ScreenCapture.VirtualScreen);
            if (bounds.Width < 2 || bounds.Height < 2) throw new CommandException("That region is off screen.");
            return new RecordArea(bounds, WindowFinder.GetForegroundProcessName());
        }
        var monitors = MonitorInfo.GetAll();
        var monitor = Screen is int n and > 0
            ? monitors.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ElementAtOrDefault(n - 1)
                ?? throw new CommandException($"There {(monitors.Count == 1 ? "is 1 screen" : $"are {monitors.Count} screens")}.")
            : monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        return new RecordArea(monitor.Bounds, null);
    }
}
