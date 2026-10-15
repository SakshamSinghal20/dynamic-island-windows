using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Island.Services;

internal static class SpotifyChecks
{
    public static void Run(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Spotify contract assertion failed."); }
        JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
        test("Spotify playlist parser handles renamed fields, nulls and local tracks", () =>
        {
            var page = SpotifyApi.ParsePage(Json("""{"items":[{"item":null},{"item":{"uri":"spotify:track:a","name":"A","artists":[{"name":"Artist"}]}},{"track":{"uri":"spotify:track:b","name":"B"}},{"item":{"uri":"spotify:track:c","is_local":true}}],"next":"https://api.spotify.com/v1/playlists/a/items?offset=4"}"""), "Playlist");
            Check(page.Items.Length == 2 && page.Items[0].Subtitle == "Artist" && page.HasMore && page.NextOffset == 4);
        });
        test("Spotify devices support null volume and restricted devices", () =>
        {
            var page = SpotifyApi.ParsePage(Json("""{"devices":[{"id":null},{"id":"device","name":"Speaker","volume_percent":null,"is_restricted":true}]}"""), "Devices");
            Check(page.Items.Length == 1 && page.Items[0].Volume == null && page.Items[0].Restricted && !page.Items[0].SupportsVolume);
        });
        test("Spotify search paginates with current ten-result limit", () =>
        {
            using var handler = new FakeHandler(_ => Reply("""{"tracks":{"items":[],"next":null}}""")); using var http = new HttpClient(handler);
            var api = new SpotifyApi(http, () => Task.FromResult("fixture")); api.Browse("Queue", "a & b", 20).GetAwaiter().GetResult();
            Check(handler.Calls.Single().Url.Contains("q=a%20%26%20b") && handler.Calls[0].Url.Contains("limit=10") && handler.Calls[0].Url.Contains("offset=20"));
        });
        test("Spotify play uses playlist context and queue uses a track URI", () =>
        {
            using var handler = new FakeHandler(_ => new(HttpStatusCode.NoContent)); using var http = new HttpClient(handler); var api = new SpotifyApi(http, () => Task.FromResult("fixture"));
            api.Play(new("spotify:playlist:abc", "Playlist", "", "playlist")).GetAwaiter().GetResult();
            api.Queue(new("spotify:track:xyz", "Track", "", "track")).GetAwaiter().GetResult();
            Check(handler.Calls[0].Method == "PUT" && Json(handler.Calls[0].Body).Text("context_uri") == "spotify:playlist:abc");
            Check(handler.Calls[1].Method == "POST" && handler.Calls[1].Url.EndsWith("me/player/queue?uri=spotify%3Atrack%3Axyz"));
        });
        test("Spotify device transfer preserves play state and clamps device volume", () =>
        {
            using var handler = new FakeHandler(_ => new(HttpStatusCode.NoContent)); using var http = new HttpClient(handler); var api = new SpotifyApi(http, () => Task.FromResult("fixture"));
            var device = new SpotifyItem("speaker", "Speaker", "", "device", SupportsVolume: true);
            api.Play(device).GetAwaiter().GetResult(); api.SetVolume(device, 101).GetAwaiter().GetResult();
            var body = Json(handler.Calls[0].Body); Check(body.GetProperty("play").ValueKind == JsonValueKind.False && body.GetProperty("device_ids")[0].GetString() == "speaker");
            Check(handler.Calls[1].Url.Contains("volume_percent=100") && handler.Calls[1].Url.Contains("device_id=speaker"));
        });
        test("Spotify saving uses generic library endpoints and unknown status cannot mutate", () =>
        {
            using var handler = new FakeHandler(i => Reply(i == 0 ? "[true]" : i == 1 ? "" : "null")); using var http = new HttpClient(handler); var api = new SpotifyApi(http, () => Task.FromResult("fixture"));
            Check(!api.ToggleSaved("spotify:track:abc").GetAwaiter().GetResult());
            Check(handler.Calls[1].Method == "DELETE" && handler.Calls[1].Url.Contains("me/library?uris="));
            bool rejected = false; try { api.ToggleSaved("spotify:track:abc").GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && handler.Calls.Count == 3);
        });
        test("Spotify rate-limit cooldown prevents repeated network requests", () =>
        {
            using var handler = new FakeHandler(_ => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromSeconds(20)); return response; }); using var http = new HttpClient(handler); var api = new SpotifyApi(http, () => Task.FromResult("fixture"));
            for (int i = 0; i < 2; i++) { try { api.Browse("Queue").GetAwaiter().GetResult(); } catch (InvalidOperationException) { } }
            Check(handler.Calls.Count == 1);
        });
        test("Spotify restricted device rejects commands before sending", () =>
        {
            using var handler = new FakeHandler(_ => new(HttpStatusCode.NoContent)); using var http = new HttpClient(handler); var api = new SpotifyApi(http, () => Task.FromResult("fixture"));
            bool rejected = false; try { api.Play(new("device", "Speaker", "", "device", Restricted: true)).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && handler.Calls.Count == 0);
        });
    }
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class FakeHandler(Func<int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public readonly List<(string Method, string Url, string Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return reply(Calls.Count - 1);
        }
    }
}
