using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Island.Services;

public sealed record Credential(string ClientId, string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset Expires, string[] Scopes, string Subject = "", string Email = "ChatGPT account");
public static class OAuth
{
    public static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string text) { string value = text.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')); }
    public static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string Url(string endpoint, Dictionary<string, string> parameters) => endpoint + "?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    public static string Text(this JsonElement value, string key, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? fallback : fallback;
    public static double Number(this JsonElement value, string key, double fallback = 0) => value.TryGetProperty(key, out var item) && item.TryGetDouble(out var result) ? result : fallback;
    public static async Task<JsonElement> Json(HttpRequestMessage request, CancellationToken cancellation = default)
    {
        using var response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        await EnsureSuccess(response, cancellation).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        return document.RootElement.Clone();
    }
    public static Task<JsonElement> Token(string endpoint, Dictionary<string, string> parameters, CancellationToken cancellation = default)
        => Json(new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(parameters) }, cancellation);
    public static Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellation = default)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Access expired. Reconnect this account.",
                HttpStatusCode.Forbidden => "The service denied access. Check your account permissions and plan.",
                HttpStatusCode.TooManyRequests => "The service's usage limit was reached. Wait before retrying.",
                _ => $"The service returned HTTP {(int)response.StatusCode}. Try again later."
            });
        return Task.CompletedTask;
    }
    public static Credential Credential(JsonElement json, string clientId, Credential? previous = null)
    {
        if (json.Text("access_token").Length == 0 || !json.Text("token_type").Equals("Bearer", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Sign-in returned no usable access token.");
        return new(clientId, json.Text("access_token"), json.Text("refresh_token", previous?.RefreshToken ?? ""), json.Text("id_token", previous?.IdToken ?? ""),
            DateTimeOffset.UtcNow.AddSeconds(json.Number("expires_in", 3600)), json.Text("scope", string.Join(" ", previous?.Scopes ?? [])).Split(' ', StringSplitOptions.RemoveEmptyEntries), previous?.Subject ?? "", previous?.Email ?? "ChatGPT account");
    }
    public static async Task<JsonElement> VerifyIdentity(string token, string audience, string nonce, CancellationToken cancellation)
    {
        string[] pieces = token.Split('.'); if (pieces.Length != 3) throw new InvalidDataException("Invalid identity token.");
        using var header = JsonDocument.Parse(Decode(pieces[0])); using var claims = JsonDocument.Parse(Decode(pieces[1]));
        if (header.RootElement.Text("alg") != "RS256") throw new InvalidDataException("Unsupported identity signature.");
        var discovery = await Json(new(HttpMethod.Get, "https://auth.openai.com/.well-known/openid-configuration"), cancellation);
        string issuer = discovery.Text("issuer"), address = discovery.Text("jwks_uri");
        if (issuer != "https://auth.openai.com" || !TrustedOpenAi(address)) throw new InvalidDataException("Invalid identity discovery endpoint.");
        var keys = await Json(new(HttpMethod.Get, address), cancellation);
        var key = keys.GetProperty("keys").EnumerateArray().FirstOrDefault(k => k.Text("kid") == header.RootElement.Text("kid") && k.Text("kty") == "RSA");
        if (key.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("No identity verification key matched.");
        using var rsa = RSA.Create(); rsa.ImportParameters(new RSAParameters { Modulus = Decode(key.Text("n")), Exponent = Decode(key.Text("e")) });
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(pieces[0] + "." + pieces[1]), Decode(pieces[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new InvalidDataException("Identity signature did not verify.");
        var c = claims.RootElement; var aud = c.GetProperty("aud");
        string[] audiences = aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [aud.GetString() ?? ""];
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (c.Text("iss") != issuer || !audiences.Contains(audience) || (audiences.Length > 1 && c.Text("azp") != audience) || c.Text("nonce") != nonce || c.Text("sub").Length == 0 || c.Number("exp") <= now || c.Number("nbf") > now + 30 || c.Number("iat", double.PositiveInfinity) > now + 30)
            throw new InvalidDataException("Sign-in identity did not match this request.");
        return c.Clone();
    }
    public static bool TrustedOpenAi(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "auth.openai.com" && uri.IsDefaultPort;
}

// TCP loopback avoids an HTTP.sys URL ACL or administrator elevation.
public sealed class OAuthLoopback : IDisposable
{
    private readonly TcpListener listener;
    public string Redirect { get; }
    private readonly string path, state;
    public OAuthLoopback(string path, string state, int port = 0)
    {
        this.path = path; this.state = state;
        listener = new(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try { listener.Start(); }
        catch (SocketException e) when (port != 0)
        {
            listener.Stop();
            throw new InvalidOperationException($"Island could not open local sign-in port {port}. Close any other app using that port, then retry.", e);
        }
        Redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{path}";
    }
    public static Dictionary<string, string>? ParseCallback(string query, string expectedState)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0].Replace('+', ' ')), value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            if (!fields.TryAdd(key, value)) return null;
        }
        return fields.GetValueOrDefault("state") == expectedState && (fields.ContainsKey("code") || fields.ContainsKey("error")) ? fields : null;
    }
    public async Task<Dictionary<string, string>> Wait(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromMinutes(3));
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var stream = client.GetStream();
            var bytes = new List<byte>(); byte[] buffer = new byte[1024];
            try
            {
                while (bytes.Count < 16384)
                {
                    int read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); if (read == 0) break;
                    bytes.AddRange(buffer.AsSpan(0, read).ToArray());
                    if (Encoding.ASCII.GetString(bytes.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                string first = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n")[0]; string[] parts = first.Split(' ');
                Dictionary<string, string>? fields = null;
                if (parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith(path + "?", StringComparison.Ordinal))
                    fields = ParseCallback(parts[1][path.Length..], state);
                bool ok = fields != null;
                string body = ok ? "Return to Island to finish signing in. You can close this tab." : "This callback did not match an Island sign-in attempt.";
                byte[] reply = Encoding.UTF8.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(reply, timeout.Token).ConfigureAwait(false);
                if (ok)
                {
                    if (fields!.ContainsKey("error")) throw new InvalidOperationException("Sign-in was declined. No connection was saved.");
                    if (string.IsNullOrEmpty(fields.GetValueOrDefault("code"))) throw new InvalidDataException("Sign-in returned an empty code.");
                    return fields;
                }
            }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }
    public void Dispose() => listener.Stop();
}
