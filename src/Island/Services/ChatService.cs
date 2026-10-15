using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Island.Core;

namespace Island.Services;

public sealed record AiModel(string Id, string Name);
public sealed class ChatService(Settings settings) : IDisposable
{
    public event Action? Changed;
    public List<Credential> Accounts { get; } = Storage.LoadCredential<List<Credential>>("chatgpt") ?? [];
    public List<AiModel> Models { get; } = [];
    public int Selected { get; private set; }
    public string ModelId { get; set; } = "";
    public string Status { get; private set; } = "Connect ChatGPT. Your messages go directly to OpenAI.";
    public bool Working { get; private set; }
    public bool SigningIn { get; private set; }
    public Credential? Account => Selected >= 0 && Selected < Accounts.Count ? Accounts[Selected] : null;
    public bool Connected => Account?.AccessToken.Length > 0;
    private readonly Dictionary<string, Conversation> conversations = [];
    public Conversation Conversation
    {
        get { string key = Account?.ClientId ?? "local"; if (!conversations.TryGetValue(key, out var chat)) conversations[key] = chat = new(); return chat; }
    }
    private CancellationTokenSource? signIn, operation, modelLoad;
    private readonly SemaphoreSlim refreshGate = new(1);
    private int revision;
    private void Notify() => Changed?.Invoke();
    public async Task Select(int index)
    {
        if (index < 0 || index >= Accounts.Count) return;
        Stop(); modelLoad?.Cancel(); revision++; Selected = index; Models.Clear(); settings.AccountId = Accounts[index].ClientId;
        await Storage.SaveSettings(settings); Notify(); await LoadModels();
    }
    public async Task SignIn(bool addAccount = false)
    {
        if (SigningIn || Storage.Diagnostic) return;
        signIn = new(); var cancellation = signIn.Token; SigningIn = true; Status = "Finish signing in in your browser."; Notify();
        var returning = addAccount ? null : Account;
        try
        {
            string state = OAuth.Random(), nonce = OAuth.Random(), verifier = OAuth.Random();
            using var server = new OAuthLoopback("/auth/callback", state);
            string requested = returning?.ClientId ?? (settings.PendingClientId.Length > 0 ? settings.PendingClientId : "dynamic_agent_client");
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = requested, ["ext_agent_host_id"] = settings.HostId, ["response_type"] = "code", ["redirect_uri"] = server.Redirect,
                ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", ["resource"] = "https://api.openai.com/v1",
                ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256", ["code_challenge"] = OAuth.Challenge(verifier)
            };
            if (requested == "dynamic_agent_client") parameters["agent_name_hint"] = "Island";
            if (!string.IsNullOrEmpty(returning?.IdToken)) parameters["id_token_hint"] = returning.IdToken;
            if (!string.IsNullOrEmpty(returning?.Email)) parameters["login_hint"] = returning.Email;
            Native.Open(OAuth.Url("https://auth.openai.com/api/accounts/authorize", parameters));
            var callback = await server.Wait(cancellation);
            string issued = callback.GetValueOrDefault("client_id", requested);
            if (issued.Length == 0 || issued == "dynamic_agent_client" || (requested != "dynamic_agent_client" && issued != requested)) throw new InvalidDataException("Sign-in returned a different client registration.");
            settings.PendingClientId = issued; await Storage.SaveSettings(settings);
            var tokens = await OAuth.Token("https://auth.openai.com/api/accounts/oauth/token", new()
            { ["grant_type"] = "authorization_code", ["client_id"] = issued, ["code"] = callback["code"], ["code_verifier"] = verifier, ["redirect_uri"] = server.Redirect, ["resource"] = "https://api.openai.com/v1" }, cancellation);
            var credential = OAuth.Credential(tokens, issued);
            var claims = await OAuth.VerifyIdentity(credential.IdToken ?? "", issued, nonce, cancellation);
            credential = credential with { Subject = claims.Text("sub"), Email = claims.Text("email", "ChatGPT account") };
            if (returning != null && returning.Subject != credential.Subject) throw new InvalidDataException("Reconnect the original account, or choose Add account.");
            cancellation.ThrowIfCancellationRequested();
            int existing = Accounts.FindIndex(a => a.ClientId == issued);
            if (existing >= 0) Accounts[existing] = credential; else { existing = Accounts.Count; Accounts.Add(credential); }
            Storage.SaveCredential("chatgpt", Accounts); settings.PendingClientId = "";
            await Select(existing);
        }
        catch (OperationCanceledException) { Status = "Sign-in cancelled or timed out. Try again when ready."; }
        catch (Exception e) { Status = e.Message; }
        finally { SigningIn = false; signIn?.Dispose(); signIn = null; Notify(); }
    }
    public void CancelSignIn() => signIn?.Cancel();
    private async Task<Credential> Access(string id, CancellationToken cancellation)
    {
        await refreshGate.WaitAsync(cancellation);
        try
        {
            var credential = Accounts.FirstOrDefault(a => a.ClientId == id && a.AccessToken.Length > 0) ?? throw new InvalidOperationException("Reconnect your ChatGPT account.");
            if (!credential.Scopes.Contains("resource.invoke") || !credential.Scopes.Contains("chatgpt.tokens.use.direct")) throw new InvalidOperationException("Reconnect and enable ChatGPT plan sharing.");
            if (credential.Expires > DateTimeOffset.UtcNow.AddSeconds(60)) return credential;
            if (string.IsNullOrEmpty(credential.RefreshToken)) throw new InvalidOperationException("Reconnect to renew access.");
            var json = await OAuth.Token("https://auth.openai.com/api/accounts/oauth/token", new() { ["grant_type"] = "refresh_token", ["client_id"] = id, ["refresh_token"] = credential.RefreshToken, ["resource"] = "https://api.openai.com/v1" }, cancellation);
            cancellation.ThrowIfCancellationRequested(); var updated = OAuth.Credential(json, id, credential);
            int index = Accounts.FindIndex(a => a.ClientId == id && a.AccessToken.Length > 0);
            if (index < 0) throw new OperationCanceledException();
            Accounts[index] = updated; Storage.SaveCredential("chatgpt", Accounts);
            if (!updated.Scopes.Contains("resource.invoke") || !updated.Scopes.Contains("chatgpt.tokens.use.direct")) throw new InvalidOperationException("ChatGPT plan sharing is no longer enabled. Reconnect to authorize it.");
            return updated;
        }
        finally { refreshGate.Release(); }
    }
    public async Task LoadModels()
    {
        if (!Connected) return;
        modelLoad?.Cancel(); modelLoad = new(); var cancellation = modelLoad.Token;
        int token = revision; string id = Account!.ClientId; Status = "Loading available models…"; Notify();
        try
        {
            var credential = await Access(id, cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models"); request.Headers.Authorization = new("Bearer", credential.AccessToken);
            var json = await OAuth.Json(request, cancellation);
            if (token != revision) return; Models.Clear();
            if (json.TryGetProperty("models", out var models))
                foreach (var model in models.EnumerateArray()) if (model.Text("visibility") == "list") Models.Add(new(model.Text("slug"), model.Text("display_name")));
            ModelId = Models.FirstOrDefault()?.Id ?? "";
            Status = Models.Count > 0 ? "Ready. Requests use your ChatGPT plan's shared limits." : "No models available for this account.";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (token == revision) Status = e.Message; }
        finally { if (token == revision) Notify(); }
    }
    public async Task Ask(string question)
    {
        string prompt = question.Trim();
        if (Working || !Connected || prompt.Length == 0) return;
        if (ModelId.Length == 0) { Status = "Load and choose an available model first."; Notify(); return; }
        if (Conversation.Messages.Sum(m => m.Text.Length) + prompt.Length > 120_000) { Status = "Start a new conversation before sending more text."; Notify(); return; }
        var chat = Conversation; var answer = chat.Begin(prompt); string id = Account!.ClientId;
        int token = ++revision; operation = new(); var cancellation = operation.Token; Working = true; Status = "Connecting…"; Notify();
        try
        {
            var credential = await Access(id, cancellation);
            var payload = new { model = ModelId, input = chat.Input, store = false, stream = true };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new("Bearer", credential.AccessToken); request.Headers.Accept.Add(new("text/event-stream"));
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await OAuth.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            await OAuth.EnsureSuccess(response, cancellation);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            var parser = new ServerEventParser(); byte[] bytes = new byte[8192]; var repaint = Stopwatch.StartNew(); bool first = true, complete = false;
            while (!complete)
            {
                int count = await stream.ReadAsync(bytes, cancellation); if (count == 0) break;
                cancellation.ThrowIfCancellationRequested(); if (token != revision) throw new OperationCanceledException();
                foreach (var frame in parser.Append(bytes.AsSpan(0, count)))
                {
                    if (frame.Data == "[DONE]") continue;
                    using var document = JsonDocument.Parse(frame.Data); var ev = document.RootElement;
                    string kind = ev.Text("type", frame.Name);
                    if (kind == "response.output_text.delta")
                    {
                        answer.Text += ev.Text("delta"); Status = "Responding…";
                        if (first || repaint.ElapsedMilliseconds >= 33) { first = false; repaint.Restart(); Notify(); }
                    }
                    else if (kind == "response.completed") { complete = true; break; }
                    else if (kind is "response.failed" or "response.incomplete" or "error") throw new InvalidOperationException("The response did not complete. Retry or check your account's usage limits.");
                }
                if (answer.Text.Length > 1_000_000) throw new InvalidDataException("Response exceeded the size limit.");
            }
            if (!complete) throw new IOException("The connection ended before the response completed.");
            answer.State = "complete"; Status = "Ready";
        }
        catch (OperationCanceledException) { answer.State = "stopped"; if (token == revision) Status = "Stopped. Partial response kept."; }
        catch (Exception e) { answer.State = "failed"; if (token == revision) Status = e.Message; }
        finally { if (token == revision) { Working = false; operation?.Dispose(); operation = null; Notify(); } }
    }
    public void Stop() { operation?.Cancel(); if (Working) { revision++; Working = false; var last = Conversation.Messages.LastOrDefault(); if (last?.State == "streaming") last.State = "stopped"; Status = "Stopped. Partial response kept."; Notify(); } }
    public async Task Retry() { if (Working) return; string? prompt = Conversation.RemoveLastExchange(); if (prompt != null) await Ask(prompt); }
    public void NewConversation() { Stop(); Conversation.Messages.Clear(); Status = "New conversation"; Notify(); }
    public void Fixture()
    {
        if (!Storage.Diagnostic) return;
        var answer = Conversation.Begin("How does Island keep its motion smooth?");
        answer.Text = "## Motion that keeps its momentum\nThe analytical spring preserves **position and velocity** when you interrupt it.\n- The host window stays fixed at the bezel.\n- Rendering follows the display clock.\n- File previews and network requests run outside the motion callback.\n\nThis is a local layout fixture. No message was sent.";
        answer.State = "complete"; Status = "Local layout fixture"; Notify();
    }
    public async Task SignOut()
    {
        var credential = Account; if (credential == null) return;
        CancelSignIn(); Stop(); modelLoad?.Cancel(); revision++;
        Accounts[Selected] = credential with { AccessToken = "", RefreshToken = null, IdToken = null, Scopes = [] };
        conversations.Remove(credential.ClientId); Models.Clear();
        try
        {
            Storage.SaveCredential("chatgpt", Accounts); Status = "Signed out on this PC."; Notify();
            if (!string.IsNullOrEmpty(credential.RefreshToken))
            {
                var discovery = await OAuth.Json(new(HttpMethod.Get, "https://auth.openai.com/.well-known/openid-configuration"));
                string endpoint = discovery.Text("revocation_endpoint"); if (!OAuth.TrustedOpenAi(endpoint)) throw new InvalidDataException();
                using var response = await OAuth.Http.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = credential.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = credential.ClientId }));
                await OAuth.EnsureSuccess(response);
            }
        }
        catch { Status = "Signed out locally. Check ChatGPT settings to confirm remote revocation."; }
        Notify();
    }
    public void Dispose() { signIn?.Cancel(); operation?.Cancel(); modelLoad?.Cancel(); }
}
