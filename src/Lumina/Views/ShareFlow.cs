using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using Lumina.Services;

namespace Lumina.Views;

/// <summary>
/// "Get link", the same everywhere: the first time, say what it does and ask; then upload (or reuse a live link
/// for the same file), copy the link and report it. Progress and the result go to a toast, or to a line of text
/// where there's no room for one (the quick preview).
/// </summary>
public static class ShareFlow
{
    private static readonly HashSet<string> Uploading = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ShareLink?> GetLinkAsync(Window? owner, string path, Toast? toast = null, Action<string>? status = null)
    {
        if (!ShareService.IsAvailable)
        {
            var choice = LuminaDialog.Show(owner, "Sharing needs a server",
                "Lumina's own share server isn't live yet. Until it is, Get link works with a share server you run yourself, "
                + "which you can set up in Settings under Sharing.", DialogKind.Info, null,
                new DialogButton("Not now", "cancel", Cancel: true),
                new DialogButton("Open Settings", "settings", Primary: true));
            if (choice == "settings") App.ShowSettings();
            return null;
        }
        if (!ShareService.CanShare(path))
        {
            LuminaDialog.Alert(owner, "Can't share this file", "Only PNG, JPEG, GIF and MP4 files can be shared.", DialogKind.Info);
            return null;
        }
        if (ShareService.ActiveLink(path) is { } existing)
        {
            Report(existing, toast, status, reused: true);
            return existing;
        }
        if (!Consent(owner)) return null;
        if (!Uploading.Add(path)) return null; // already on its way

        try
        {
            status?.Invoke("Uploading...");
            toast?.ShowProgress($"Uploading to {ShareService.ServerHost}...");
            var progress = new Progress<double>(p =>
            {
                toast?.SetProgress(p);
                status?.Invoke($"Uploading {p:P0}");
            });
            var link = await ShareService.UploadAsync(path, progress);
            Report(link, toast, status, reused: false);
            return link;
        }
        catch (Exception ex)
        {
            if (ex is not ShareException) Log.Error(ex, "Sharing a file");
            toast?.Hide();
            status?.Invoke("Couldn't share");
            LuminaDialog.Alert(owner, "Couldn't get a link", ex is ShareException ? ex.Message : "Something went wrong while uploading. Try again in a moment.");
            return null;
        }
        finally
        {
            Uploading.Remove(path);
        }
    }

    /// <summary>Takes a link down, after checking. Returns true when it's gone.</summary>
    public static async Task<bool> DeleteLinkAsync(Window? owner, ShareLink link, Toast? toast = null)
    {
        var choice = LuminaDialog.Show(owner, "Delete the link?",
            $"{link.Url} will stop working for everyone straight away. Your file stays where it is.", DialogKind.Question, null,
            new DialogButton("Cancel", "cancel", Cancel: true),
            new DialogButton("Delete link", "delete", Danger: true));
        if (choice != "delete") return false;

        if (await ShareService.DeleteAsync(link))
        {
            toast?.Show("Link deleted");
            return true;
        }
        LuminaDialog.Alert(owner, "Couldn't delete the link", $"Lumina couldn't reach {new Uri(link.Server).Host}. Try again in a moment.");
        return false;
    }

    /// <summary>"in 3 hours", "in 6 days".</summary>
    public static string Remaining(DateTime expiresUtc)
    {
        var left = expiresUtc - DateTime.UtcNow;
        if (left.TotalMinutes < 90) return $"in {Math.Max(1, (int)Math.Round(left.TotalMinutes))} minutes";
        if (left.TotalHours < 36) return $"in {(int)Math.Round(left.TotalHours)} hours";
        return $"in {(int)Math.Round(left.TotalDays)} days";
    }

    public static string Lifetime(int hours) => hours switch
    {
        1 => "1 hour",
        < 24 => $"{hours} hours",
        24 => "1 day",
        168 => "1 week",
        _ => $"{hours / 24} days",
    };

    public static void Open(ShareLink link)
    {
        try { Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex, "Opening a share link"); }
    }

    private static void Report(ShareLink link, Toast? toast, Action<string>? status, bool reused)
    {
        bool copied = ClipboardService.CopyText(link.Url);
        string what = copied ? "Link copied" : "Link ready";
        string when = $"expires {Remaining(link.ExpiresAt)}";
        status?.Invoke($"{what}, {when}");
        toast?.Show(reused ? $"{what} (already shared), {when}" : $"{what}, {when}", "Open", () => Open(link), seconds: 6);
    }

    private static bool Consent(Window? owner)
    {
        if (App.Settings.ShareConsent) return true;
        string where = ShareService.UsesOwnServer ? $"your server ({ShareService.ServerHost})" : $"Lumina's share server ({ShareService.ServerHost})";
        var choice = LuminaDialog.Show(owner, "Share with a link?",
            $"Lumina uploads the file to {where} and copies a link to it. Anyone with the link can open it until it expires "
            + $"(after {Lifetime(ShareService.ExpiryHours)}, you can change this in Settings). You can delete a link sooner from the gallery.\n\n"
            + "Nothing is ever uploaded unless you choose Get link.",
            DialogKind.Question, null,
            new DialogButton("Cancel", "cancel", Cancel: true),
            new DialogButton("Upload and copy link", "upload", Primary: true));
        if (choice != "upload") return false;
        App.Settings.ShareConsent = true;
        SettingsService.Save(App.Settings);
        return true;
    }
}
