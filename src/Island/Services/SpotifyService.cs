using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Island.Services;

public sealed class SpotifyService(Settings settings) : IDisposable
{
    public const string RedirectUri = "http://127.0.0.1:8888/spotify/callback";
    public static OAuthLoopback CreateCallback(string state)
    {
        var uri = new Uri(RedirectUri);
        return new OAuthLoopback(uri.AbsolutePath, state, uri.Port);
    }
    private SpotifyApi? api;
    public SpotifyApi Api => api ??= new(OAuth.Http, Access);
    private Credential? credential = Storage.LoadCredential<Credential>("spotify");
    private CancellationTokenSource? authorization;
    private readonly SemaphoreSlim gate = new(1);
    private int revision;
    public bool Connected => credential != null;
    public bool SigningIn => authorization != null;
    public event Action? Changed;
    public async Task Authorize(string clientId)
    {
        if (authorization != null) return;
        if (clientId.Length != 32 || !clientId.All(Uri.IsHexDigit)) throw new InvalidOperationException($"Enter the 32-character Spotify Client ID. Register {RedirectUri} in your Spotify app.");
        settings.SpotifyClientId = clientId; await Storage.SaveSettings(settings);
        authorization = new(); var cancellation = authorization.Token; int token = revision;
        try
        {
            string state = OAuth.Random(), verifier = OAuth.Random(); using var server = CreateCallback(state);
            Native.Open(OAuth.Url("https://accounts.spotify.com/authorize", new()
            {
                ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = server.Redirect,
                ["scope"] = "user-read-playback-state user-modify-playback-state user-read-currently-playing user-read-recently-played user-library-read user-library-modify playlist-read-private playlist-read-collaborative",
                ["state"] = state, ["code_challenge_method"] = "S256", ["code_challenge"] = OAuth.Challenge(verifier)
            }));
            var result = await server.Wait(cancellation);
            var json = await OAuth.Token("https://accounts.spotify.com/api/token", new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = result["code"], ["code_verifier"] = verifier, ["redirect_uri"] = server.Redirect }, cancellation);
            if (token != revision) return; credential = OAuth.Credential(json, clientId); Storage.SaveCredential("spotify", credential); Changed?.Invoke();
        }
        finally { authorization?.Dispose(); authorization = null; }
    }
    private async Task<string> Access()
    {
        await gate.WaitAsync();
        try
        {
            var current = credential ?? throw new InvalidOperationException("Connect the Spotify library from Settings first.");
            if (current.Expires > DateTimeOffset.UtcNow.AddSeconds(60)) return current.AccessToken;
            if (string.IsNullOrEmpty(current.RefreshToken)) throw new InvalidOperationException("Reconnect the Spotify library.");
            int token = revision;
            var json = await OAuth.Token("https://accounts.spotify.com/api/token", new() { ["grant_type"] = "refresh_token", ["client_id"] = current.ClientId, ["refresh_token"] = current.RefreshToken });
            if (token != revision) throw new OperationCanceledException();
            credential = OAuth.Credential(json, current.ClientId, current); Storage.SaveCredential("spotify", credential); return credential.AccessToken;
        }
        finally { gate.Release(); }
    }
    public async Task ToggleSaved()
    {
        var current = await Api.Request("me/player/currently-playing");
        if (current == null || !current.Value.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Play a Spotify track first.");
        string uri = item.Text("uri");
        await Api.ToggleSaved(uri);
    }
    public void Disconnect() { revision++; authorization?.Cancel(); credential = null; Storage.RemoveCredential("spotify"); Changed?.Invoke(); }
    public void Dispose() => authorization?.Cancel();
}
