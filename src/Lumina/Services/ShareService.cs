using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Lumina.Services;

/// <summary>A short link to a file uploaded to a share server, and what's needed to take it down again.</summary>
public sealed record ShareLink(
    string Path, string Url, string Id, string DeleteKey, string Server, DateTime ExpiresAt, long Length, DateTime Modified)
{
    [JsonIgnore] public bool IsActive => ExpiresAt > DateTime.UtcNow.AddMinutes(1);
}

/// <summary>Something went wrong in a way worth telling the user about, in words they can act on.</summary>
public sealed class ShareException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Uploads captures to a Lumina share server (share-server/ in the repository: a Cloudflare Worker on R2) and keeps
/// a list of the links handed out, so a file that's already shared reuses its link and can be taken down later.
/// Nothing is uploaded unless the user asks for a link.
/// </summary>
public static class ShareService
{
    /// <summary>
    /// The server everyone uses unless they set up their own in Settings. Null until Lumina's own server is live:
    /// until then Get link only shows up for people who set up their own.
    /// </summary>
    public const string? DefaultServer = null;

    public const string SetupGuide = "https://github.com/itspixxel/Lumina/tree/amethyst/share-server#readme";

    /// <summary>The link lifetimes Settings offers, in hours.</summary>
    public static readonly int[] ExpiryChoices = { 1, 24, 168 };

    /// <summary>The server's limit. It reports its own in error replies; this is for checking before uploading.</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly HttpClient Http = CreateClient();
    private static List<ShareLink>? _links;

    private static string LinksFile => System.IO.Path.Combine(AppPaths.DataDir, "shares.json");

    /// <summary>Raised on the calling thread whenever a link is added or removed.</summary>
    public static event Action? Changed;

    public static bool UsesOwnServer => !string.IsNullOrWhiteSpace(App.Settings.ShareServer);

    /// <summary>There's a server to upload to, so Get link can be offered.</summary>
    public static bool IsAvailable => UsesOwnServer || DefaultServer != null;

    public static string Server => ((UsesOwnServer ? App.Settings.ShareServer : DefaultServer) ?? "").Trim().TrimEnd('/');

    /// <summary>The host links point at, for telling the user where a file goes.</summary>
    public static string ServerHost => Uri.TryCreate(Server, UriKind.Absolute, out var uri) ? uri.Host : Server;

    public static int ExpiryHours => ExpiryChoices.Contains(App.Settings.ShareExpiryHours) ? App.Settings.ShareExpiryHours : 24;

    public static bool CanShare(string path) => ContentType(path) != null;

    /// <summary>A live link for this exact file (same contents on disk, same server), if one was made.</summary>
    public static ShareLink? ActiveLink(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        return Links.LastOrDefault(l => l.IsActive && SamePath(l.Path, path) && l.Server == Server
                                        && l.Length == info.Length && l.Modified == info.LastWriteTimeUtc);
    }

    /// <summary>Every live link, newest first.</summary>
    public static IReadOnlyList<ShareLink> ActiveLinks() => Links.Where(l => l.IsActive).Reverse().ToList();

    public static async Task<ShareLink> UploadAsync(string path, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        string type = ContentType(path) ?? throw new ShareException("Only PNG, JPEG, GIF and MP4 files can be shared.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new ShareException("The file isn't there any more.");
        if (info.Length > MaxBytes)
            throw new ShareException($"Files up to {MaxBytes / 1024 / 1024} MB can be shared, and this one is {info.Length / 1024 / 1024} MB."
                                     + (type == "video/mp4" ? " Trim it, or export a smaller GIF." : ""));
        if (!Uri.TryCreate(Server + "/api/upload", UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http"))
            throw new ShareException("The share server's address isn't valid. Check it in Settings.");

        string server = Server;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
        request.Content = new ProgressContent(file, info.Length, progress);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        request.Headers.Add("X-File-Name", Uri.EscapeDataString(System.IO.Path.GetFileName(path)));
        request.Headers.Add("X-Expires", (ExpiryHours * 3600).ToString());
        if (UsesOwnServer && !string.IsNullOrWhiteSpace(App.Settings.ShareToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", App.Settings.ShareToken.Trim());

        UploadReply reply;
        try
        {
            using var response = await Http.SendAsync(request, cancel);
            string body = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode) throw new ShareException(ErrorMessage(response.StatusCode, body));
            reply = JsonSerializer.Deserialize<UploadReply>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new ShareException("The share server sent back something Lumina doesn't understand.");
            if (string.IsNullOrEmpty(reply.Url) || string.IsNullOrEmpty(reply.Id))
                throw new ShareException("The share server sent back something Lumina doesn't understand.");
        }
        catch (HttpRequestException ex)
        {
            throw new ShareException($"Couldn't reach {ServerHost}. Check your connection and try again.", ex);
        }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new ShareException("The upload took too long. Check your connection and try again.", ex);
        }
        catch (JsonException ex)
        {
            throw new ShareException("The share server sent back something Lumina doesn't understand.", ex);
        }

        var link = new ShareLink(path, reply.Url, reply.Id, reply.DeleteKey ?? "", server,
            reply.ExpiresAt?.ToUniversalTime() ?? DateTime.UtcNow.AddHours(ExpiryHours), info.Length, info.LastWriteTimeUtc);
        Links.Add(link);
        Save();
        Log.Info($"Shared {System.IO.Path.GetFileName(path)} as {link.Url}");
        return link;
    }

    /// <summary>Takes a link down. Returns false if the server couldn't be reached; the link is kept so it can be tried again.</summary>
    public static async Task<bool> DeleteAsync(ShareLink link)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{link.Server}/api/files/{Uri.EscapeDataString(link.Id)}");
            request.Headers.Add("X-Delete-Key", link.DeleteKey);
            using var response = await Http.SendAsync(request);
            // 403 means the key no longer matches, so the file is somebody else's now (or gone): forget it either way.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Forbidden)
            {
                Log.Info($"Deleting {link.Url} failed: {(int)response.StatusCode}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Deleting a share link");
            return false;
        }
        Links.RemoveAll(l => l.Id == link.Id && l.Server == link.Server);
        Save();
        return true;
    }

    // ---------- Details ----------

    private sealed record UploadReply(string? Id, string? Url, string? DeleteKey, DateTime? ExpiresAt);

    private static string ErrorMessage(HttpStatusCode status, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) message = e.GetString();
        }
        catch (JsonException)
        {
            // Not one of ours: a proxy page, most likely.
        }
        return status switch
        {
            HttpStatusCode.Unauthorized => "The share server didn't accept the token. Check it in Settings.",
            _ when message != null => message,
            // Not our JSON: some other website, or a proxy page in the way.
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
                => $"{ServerHost} doesn't look like a Lumina share server. Check the address in Settings.",
            _ => $"The share server couldn't take the file ({(int)status}). Try again later.",
        };
    }

    private static string? ContentType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        _ => null,
    };

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Lumina/{Updater.Current.ToString(3)}");
        return http;
    }

    private static List<ShareLink> Links => _links ??= Load();

    private static List<ShareLink> Load()
    {
        try
        {
            if (File.Exists(LinksFile))
            {
                var links = JsonSerializer.Deserialize<List<ShareLink>>(File.ReadAllText(LinksFile), Json) ?? new();
                // Expired links are gone from the server already.
                return links.Where(l => l.IsActive).ToList();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading share links");
        }
        return new();
    }

    private static void Save()
    {
        try
        {
            Links.RemoveAll(l => !l.IsActive);
            string tmp = LinksFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Links, Json));
            File.Move(tmp, LinksFile, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving share links");
        }
        Changed?.Invoke();
    }

    /// <summary>Streams the file in chunks, reporting how much has gone.</summary>
    private sealed class ProgressContent(Stream source, long length, IProgress<double>? progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancel)
        {
            var buffer = new byte[81920];
            long sent = 0;
            int read;
            source.Position = 0;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancel);
                sent += read;
                progress?.Report(length > 0 ? (double)sent / length : 1);
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }
}
