using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Island.Services;

public sealed record SpotifyItem(string Uri, string Title, string Subtitle, string Kind, bool Restricted = false, int? Volume = null, bool SupportsVolume = false);
public sealed record SpotifyPage(SpotifyItem[] Items, bool HasMore, int NextOffset);

// Separate transport makes response compatibility, commands and failures testable without an account.
public sealed class SpotifyApi(HttpClient http, Func<Task<string>> access)
{
    private DateTimeOffset retryAt;
    private string artworkKey = "";
    private byte[]? artworkBytes;
    private DateTimeOffset artworkRetryAt;
    public async Task<byte[]?> Artwork(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;
        string key = title + "\0" + artist;
        if (key == artworkKey && (artworkBytes != null || DateTimeOffset.UtcNow < artworkRetryAt)) return artworkBytes;
        artworkKey = key; artworkBytes = null; artworkRetryAt = DateTimeOffset.UtcNow.AddSeconds(15);
        try
        {
            var state = await Request("me/player/currently-playing");
            if (state == null || !state.Value.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object ||
                !item.Text("name").Equals(title, StringComparison.OrdinalIgnoreCase) ||
                !item.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array) return null;
            var names = artists.EnumerateArray().Select(a => a.Text("name")).ToArray();
            if (!names.Contains(artist, StringComparer.OrdinalIgnoreCase) && !string.Join(", ", names).Equals(artist, StringComparison.OrdinalIgnoreCase)) return null;
            if (!item.TryGetProperty("album", out var album) || !album.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
            string url = images.EnumerateArray().Select(i => i.Text("url")).FirstOrDefault(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "i.scdn.co") ?? "";
            if (url.Length == 0) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            // Album artwork is public. Keep the Spotify access token on API requests only.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 8_000_000) return null;
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var memory = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (memory.Length + read > 8_000_000) return null;
                memory.Write(buffer, 0, read);
            }
            byte[] bytes = memory.ToArray();
            if (artworkKey == key && bytes.Length > 0) artworkBytes = bytes;
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException) { return null; }
    }
    public async Task<JsonElement?> Request(string path, HttpMethod? method = null, Dictionary<string, string>? parameters = null, object? body = null, CancellationToken cancellation = default)
    {
        if (DateTimeOffset.UtcNow < retryAt) throw new InvalidOperationException($"Spotify is rate limiting requests. Retry in {Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds)} seconds.");
        string token = await access();
        string url = "https://api.spotify.com/v1/" + path;
        if (parameters != null) url = OAuth.Url(url, parameters);
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        request.Headers.Authorization = new("Bearer", token);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            retryAt = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30));
            throw new InvalidOperationException("Spotify's request limit was reached. Wait before refreshing.");
        }
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Open Spotify and start a track, then retry. The selected device or item may be unavailable.");
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("Spotify denied this action. Check Premium, app access and granted permissions; some playlists cannot be browsed by development apps.");
        await OAuth.EnsureSuccess(response, timeout.Token);
        // Player/library mutations have no JSON result. Spotify can acknowledge
        // them with 200/202 and an opaque command id as well as empty 204.
        if (request.Method != HttpMethod.Get) return null;
        string text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { using var json = JsonDocument.Parse(text); return json.RootElement.Clone(); }
        catch (JsonException e) { throw new InvalidOperationException("Spotify returned an unreadable response. Try again.", e); }
    }
    public async Task<SpotifyPage> Browse(string mode, string query = "", int offset = 0, string context = "", CancellationToken cancellation = default)
    {
        bool search = !string.IsNullOrWhiteSpace(query);
        string path = search ? "search" : mode switch
        {
            "Queue" => "me/player/queue", "Recent" => "me/player/recently-played", "Liked" => "me/tracks",
            "Playlists" => "me/playlists", "Devices" => "me/player/devices",
            "Playlist" => "playlists/" + ItemId(context, "playlist") + "/items",
            _ => throw new ArgumentException("Unknown Spotify view.")
        };
        Dictionary<string, string>? parameters = null;
        if (search) parameters = new() { ["q"] = query.Trim(), ["type"] = "track", ["limit"] = "10", ["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        else if (mode is "Liked" or "Playlists" or "Playlist") parameters = new() { ["limit"] = "20", ["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        else if (mode == "Recent") parameters = new() { ["limit"] = "50" };
        var json = await Request(path, parameters: parameters, cancellation: cancellation);
        return json == null ? new([], false, offset) : ParsePage(json.Value, search ? "Search" : mode, offset);
    }
    public static SpotifyPage ParsePage(JsonElement root, string mode, int offset = 0)
    {
        if (root.ValueKind != JsonValueKind.Object) return new([], false, offset);
        if (mode == "Search" && root.TryGetProperty("tracks", out var tracks)) root = tracks;
        string property = mode == "Queue" ? "queue" : mode == "Devices" ? "devices" : "items";
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return new([], false, offset);
        var items = new List<SpotifyItem>();
        foreach (var wrapper in array.EnumerateArray())
        {
            var item = wrapper;
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (mode is "Recent" or "Liked" or "Playlist")
            {
                if (!wrapper.TryGetProperty("item", out item) && !wrapper.TryGetProperty("track", out item)) continue;
                if (item.ValueKind != JsonValueKind.Object) continue;
            }
            if (mode == "Devices")
            {
                string id = item.Text("id"); if (id.Length == 0) continue;
                bool active = Flag(item, "is_active");
                items.Add(new(id, item.Text("name", "Spotify device"), (active ? "Playing here · " : "") + item.Text("type"), "device", Flag(item, "is_restricted"),
                    item.TryGetProperty("volume_percent", out var volume) && volume.ValueKind == JsonValueKind.Number && volume.TryGetInt32(out int percent) ? percent : null, Flag(item, "supports_volume")));
                continue;
            }
            string uri = item.Text("uri");
            string kind = uri.StartsWith("spotify:playlist:", StringComparison.Ordinal) ? "playlist" : uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? "track" : uri.StartsWith("spotify:episode:", StringComparison.Ordinal) ? "episode" : "";
            if (kind.Length == 0 || Flag(item, "is_local")) continue;
            string subtitle = item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array ? string.Join(", ", artists.EnumerateArray().Select(a => a.Text("name"))) :
                item.TryGetProperty("owner", out var owner) ? owner.Text("display_name", "Playlist") : "Episode";
            items.Add(new(uri, item.Text("name", "Untitled"), subtitle, kind));
        }
        bool more = mode is not ("Queue" or "Devices" or "Recent") && root.Text("next").Length > 0;
        return new(items.ToArray(), more, offset + array.GetArrayLength());
    }
    private static bool Flag(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    public static string ItemId(string uri, string kind)
    {
        string prefix = "spotify:" + kind + ":";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal) || uri.Length == prefix.Length || !uri[prefix.Length..].All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Select a valid Spotify item.");
        return uri[prefix.Length..];
    }
    public async Task Play(SpotifyItem item)
    {
        if (item.Restricted) throw new InvalidOperationException("Spotify does not allow controlling this device.");
        if (item.Kind == "device") { await Request("me/player", HttpMethod.Put, body: new { device_ids = new[] { item.Uri }, play = false }); return; }
        ItemId(item.Uri, item.Kind);
        await Request("me/player/play", HttpMethod.Put, body: item.Kind == "playlist" ? new { context_uri = item.Uri } : (object)new { uris = new[] { item.Uri } });
    }
    public async Task Queue(SpotifyItem item)
    {
        if (item.Kind is not ("track" or "episode")) throw new InvalidOperationException("Select a track or episode to add to the queue.");
        ItemId(item.Uri, item.Kind); await Request("me/player/queue", HttpMethod.Post, new() { ["uri"] = item.Uri });
    }
    public async Task<bool> ToggleSaved(string uri)
    {
        ItemId(uri, "track"); var saved = await Request("me/library/contains", parameters: new() { ["uris"] = uri });
        if (saved == null || saved.Value.ValueKind != JsonValueKind.Array || saved.Value.GetArrayLength() != 1 || saved.Value[0].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("Spotify did not return the saved status. Retry before changing your library.");
        bool exists = saved.Value[0].ValueKind == JsonValueKind.True;
        await Request("me/library", exists ? HttpMethod.Delete : HttpMethod.Put, new() { ["uris"] = uri }); return !exists;
    }
    public async Task SetVolume(SpotifyItem device, double value)
    {
        if (device.Restricted || !device.SupportsVolume) throw new InvalidOperationException("This Spotify device does not expose volume control.");
        await Request("me/player/volume", HttpMethod.Put, new() { ["device_id"] = device.Uri, ["volume_percent"] = ((int)Math.Round(Math.Clamp(value, 0, 100))).ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }
}
