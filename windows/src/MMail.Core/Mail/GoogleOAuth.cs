using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace MMail.Core.Mail;

/// The Google OAuth client MMail signs in with ("Desktop app" in Google Cloud). Baked in at
/// build time from the GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET repository secrets; the
/// MMAIL_GOOGLE_CLIENT_ID / MMAIL_GOOGLE_CLIENT_SECRET environment variables override it.
/// A desktop client's secret is not confidential (Google says so): it only names the app.
public sealed record GoogleClient(string Id, string Secret)
{
    public static GoogleClient? Default { get; } = Load();

    private static GoogleClient? Load()
    {
        string? Meta(string key) => typeof(GoogleClient).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
        var id = Environment.GetEnvironmentVariable("MMAIL_GOOGLE_CLIENT_ID") ?? Meta("GoogleClientId");
        var secret = Environment.GetEnvironmentVariable("MMAIL_GOOGLE_CLIENT_SECRET") ?? Meta("GoogleClientSecret");
        return string.IsNullOrWhiteSpace(id) ? null : new GoogleClient(id.Trim(), secret?.Trim() ?? "");
    }
}

public sealed record GoogleSignIn(string Email, string Name, string RefreshToken);

/// Google refused the saved sign-in (revoked, or the password changed): sign in again.
public sealed class GoogleSignInExpiredException(string message) : Exception(message);

/// "Sign in with Google" for an installed app: the browser opens Google's page, Google sends
/// the code back to a one-shot listener on 127.0.0.1 (RFC 8252, with PKCE), and MMail keeps the
/// refresh token. IMAP and SMTP then log in with XOAUTH2 and a short-lived access token.
public static class GoogleOAuth
{
    public const string Scopes = "openid email profile https://mail.google.com/";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<GoogleSignIn> SignInAsync(GoogleClient client, Action<Uri> openBrowser, CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        openBrowser(AuthorizeUri(client, redirect, verifier, state));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var code = await ReceiveCodeAsync(listener, state, timeout.Token);

        var tokens = await PostAsync(new()
        {
            ["code"] = code, ["client_id"] = client.Id, ["client_secret"] = client.Secret,
            ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier,
        }, ct);
        var refresh = Str(tokens, "refresh_token")
                      ?? throw new InvalidOperationException("Google did not grant offline access. Try again.");
        var (email, name) = ReadIdToken(Str(tokens, "id_token") ?? "");
        if (email.Length == 0) throw new InvalidOperationException("Google did not say which account was chosen.");
        return new GoogleSignIn(email, name, refresh);
    }

    /// A fresh access token and when it expires.
    public static async Task<(string Token, DateTimeOffset Expires)> RefreshAsync(GoogleClient client, string refreshToken, CancellationToken ct)
    {
        var tokens = await PostAsync(new()
        {
            ["refresh_token"] = refreshToken, ["client_id"] = client.Id, ["client_secret"] = client.Secret,
            ["grant_type"] = "refresh_token",
        }, ct);
        var expiresIn = tokens.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        return (Str(tokens, "access_token") ?? throw new InvalidOperationException("Google sent no access token."),
                DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    public static Uri AuthorizeUri(GoogleClient client, string redirect, string verifier, string state)
    {
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = client.Id, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = Scopes,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = state,
            // A refresh token every time, so a re-sign-in always repairs the account.
            ["access_type"] = "offline", ["prompt"] = "consent select_account",
        }.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return new Uri($"{AuthEndpoint}?{query}");
    }

    /// Waits for the browser to come back to the listener with ?code=…&state=…, and answers it
    /// with a page telling the user to go back to MMail.
    public static async Task<string> ReceiveCodeAsync(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            await using var stream = socket.GetStream();
            var requestLine = await ReadLineAsync(stream, ct);
            // "GET /?code=…&state=… HTTP/1.1"; browsers also ask for /favicon.ico.
            var target = requestLine.Split(' ') is [_, var t, ..] ? t : "/";
            var query = HttpUtility.ParseQueryString(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
            if (query["code"] is null && query["error"] is null)
            {
                await RespondAsync(stream, 404, "", ct);
                continue;
            }
            if (query["error"] is { } error)
            {
                await RespondAsync(stream, 200, Page("Connexion annulée", "Tu peux fermer cet onglet et revenir dans MMail."), ct);
                throw new OperationCanceledException(error == "access_denied" ? "Sign-in cancelled." : $"Google: {error}");
            }
            if (query["state"] != state)
            {
                await RespondAsync(stream, 400, Page("Lien expiré", "Recommence la connexion depuis MMail."), ct);
                continue;
            }
            await RespondAsync(stream, 200, Page("C'est bon !", "MMail est connecté à ton compte Google. Tu peux fermer cet onglet."), ct);
            return query["code"]!;
        }
    }

    /// The email and name in an OpenID id_token (its payload, already checked by Google's TLS).
    public static (string Email, string Name) ReadIdToken(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2) return ("", "");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return (Str(doc.RootElement, "email") ?? "", Str(doc.RootElement, "name") ?? "");
    }

    private static async Task<JsonElement> PostAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}");
        if (!response.IsSuccessStatusCode)
        {
            var error = Str(doc.RootElement, "error") ?? response.StatusCode.ToString();
            if (error == "invalid_grant")
                throw new GoogleSignInExpiredException("Google no longer accepts this sign-in. Remove the account and sign in with Google again.");
            throw new InvalidOperationException($"Google sign-in failed: {Str(doc.RootElement, "error_description") ?? error}");
        }
        return doc.RootElement.Clone();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (line.Length < 8192 && await stream.ReadAsync(buffer, ct) == 1 && buffer[0] != '\n')
            if (buffer[0] != '\r') line.Append((char)buffer[0]);
        return line.ToString();
    }

    private static async Task RespondAsync(Stream stream, int status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }

    private static string Page(string title, string text) => $$"""
        <!doctype html><html lang="fr"><head><meta charset="utf-8"><title>MMail</title>
        <style>body{margin:0;height:100vh;display:grid;place-items:center;background:#f5f4ed;color:#262624;
        font:16px "Segoe UI",system-ui,sans-serif}main{text-align:center}h1{font:600 28px Georgia,serif;margin:0 0 8px}
        p{color:#5e5d59;margin:0}i{display:block;width:12px;height:12px;border-radius:6px;background:#d97757;margin:0 auto 20px}</style>
        </head><body><main><i></i><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(text)}}</p></main></body></html>
        """;
}

/// Short-lived access tokens per account, refreshed from the saved refresh token as needed.
public sealed class GoogleTokens
{
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _cache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> AccessTokenAsync(string accountId, string refreshToken, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(accountId, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(2))
                return cached.Token;
            var client = GoogleClient.Default
                         ?? throw new InvalidOperationException("This build of MMail has no Google sign-in configured.");
            var fresh = await GoogleOAuth.RefreshAsync(client, refreshToken, ct);
            _cache[accountId] = fresh;
            return fresh.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Forget(string accountId)
    {
        _gate.Wait();
        try { _cache.Remove(accountId); }
        finally { _gate.Release(); }
    }
}
