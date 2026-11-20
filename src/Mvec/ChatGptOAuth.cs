using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mvec;

internal sealed record ChatGptEndpoints(string Authorize, string Token, string Jwks, string Responses, string Models, string Revoke)
{
    private const string Accounts = "https://auth.openai.com/api/accounts";
    public static ChatGptEndpoints Official { get; } = new(
        Accounts + "/authorize", Accounts + "/oauth/token", "https://auth.openai.com/.well-known/jwks.json",
        ChatGptOAuth.Resource + "/responses", ChatGptOAuth.Resource + "/models", Accounts + "/oauth/revoke");
}

internal sealed class ChatGptOAuth(Project project, HttpClient http, ChatGptEndpoints endpoints)
{
    public const string Resource = "https://api.openai.com/v1";
    public const string Issuer = "https://auth.openai.com";
    public const string PlanScope = "chatgpt.tokens.use.direct";
    private const string RegistrationClient = "dynamic_agent_client";
    private const string RequestedScopes = "openid profile email offline_access resource.invoke " + PlanScope;
    private const string CallbackPath = "/auth/callback";
    private string SessionPath => Path.Join(project.State, "chatgpt.session");

    public async Task<string> SignInAsync(int port, Action<Uri> authorize, CancellationToken ct)
    {
        using var sessionLock = LockSession();
        var saved = ReadSession() ?? new ChatGptSession($"urn:uuid:{Guid.NewGuid()}");
        if (saved.ClientId is null)
            WriteSession(saved);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{CallbackPath}";
            var state = RandomValue();
            var nonce = RandomValue();
            var verifier = RandomValue();
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = saved.ClientId ?? RegistrationClient, ["ext_agent_host_id"] = saved.Host,
                ["response_type"] = "code", ["redirect_uri"] = redirect, ["scope"] = RequestedScopes,
                ["resource"] = Resource, ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256",
                ["code_challenge"] = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            };
            if (saved.ClientId is null)
                parameters["agent_name_hint"] = "mvec";
            // Do not put a retained ID token in this URL: headless users copy it to their browser.
            authorize(new Uri(endpoints.Authorize + "?" + string.Join('&',
                parameters.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)))));

            using var callback = await ReceiveAsync(listener, state, timeout.Token);
            try
            {
                if (callback.Get("error") is { } error)
                    throw new ChatGptException("chatgpt_sign_in_denied", $"ChatGPT authorization did not complete ({error}).");
                var code = callback.Get("code") ?? throw new InvalidDataException("ChatGPT did not return an authorization code.");
                var issued = callback.Get("client_id");
                var client = saved.ClientId ?? issued;
                if (string.IsNullOrEmpty(client) || client == RegistrationClient || issued is not null && issued != client)
                    throw new InvalidDataException("ChatGPT returned a missing or unexpected client ID.");

                var token = await TokenAsync(new()
                {
                    ["client_id"] = client, ["grant_type"] = "authorization_code", ["code"] = code,
                    ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["resource"] = Resource
                }, timeout.Token);
                var identity = await ValidateIdentityAsync(token.IdToken, client, nonce, timeout.Token);
                if (saved.Subject is not null && saved.Subject != identity.Subject)
                    throw new InvalidDataException("ChatGPT returned a different account for this registration.");
                WriteSession(WithToken(saved with
                {
                    ClientId = client, Subject = identity.Subject, Email = identity.Email
                }, token));
                await callback.ReplyAsync("200 OK", "You can close this tab. mvec is signed in.", timeout.Token);
                return identity.Email ?? "ChatGPT account";
            }
            catch
            {
                await callback.ReplyAsync("400 Bad Request", "Sign-in did not complete. See the mvec terminal.", timeout.Token);
                throw;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("ChatGPT sign-in timed out. Run mvec login again.");
        }
        finally { listener.Stop(); }
    }

    public async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        var saved = ReadSession();
        if (saved?.AccessToken is null)
            throw SignInRequired();
        if (saved.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return saved.AccessToken;

        using var sessionLock = LockSession();
        saved = ReadSession();
        if (saved?.AccessToken is null || saved.RefreshToken is null || saved.ClientId is null)
            throw SignInRequired();
        if (saved.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return saved.AccessToken;

        var token = await TokenAsync(new()
        {
            ["client_id"] = saved.ClientId, ["grant_type"] = "refresh_token",
            ["refresh_token"] = saved.RefreshToken, ["resource"] = Resource
        }, ct, saved.Scope);
        var updated = WithToken(saved, token);
        WriteSession(updated);
        return updated.AccessToken!;
    }

    public async Task<bool> SignOutAsync(CancellationToken ct)
    {
        using var sessionLock = LockSession();
        var saved = ReadSession();
        if (saved?.RefreshToken is null || saved.ClientId is null)
            return true;
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = saved.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = saved.ClientId
            });
            using var response = await http.PostAsync(endpoints.Revoke, form, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        finally
        {
            WriteSession(saved with { AccessToken = null, RefreshToken = null, Scope = null, ExpiresAt = default });
        }
    }

    public async Task<(string Slug, string Name)[]> ModelsAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoints.Models);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        return json.RootElement.GetProperty("models").EnumerateArray()
            .Where(model => Text(model, "visibility") == "list" && Text(model, "slug") is not null)
            .Select(model => (Text(model, "slug")!, Text(model, "display_name") ?? Text(model, "slug")!))
            .ToArray();
    }

    private async Task<ChatGptToken> TokenAsync(Dictionary<string, string> fields, CancellationToken ct, string? previousScope = null)
    {
        using var form = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(endpoints.Token, form, ct);
        await EnsureSuccessAsync(response, ct);
        var token = await response.Content.ReadFromJsonAsync(Wire.Default.ChatGptToken, ct)
            ?? throw new InvalidDataException("ChatGPT returned an empty token response.");
        var scope = token.Scope ?? previousScope ?? "";
        if (!scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(PlanScope, StringComparer.Ordinal))
            throw new ChatGptException("chatgpt_plan_not_granted", "ChatGPT plan usage was not granted. Run mvec login and approve plan usage.");
        if (!string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || token.ExpiresIn <= 0
            || string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new InvalidDataException("ChatGPT returned an incomplete renewable session.");
        return token with { Scope = scope };
    }

    private static ChatGptSession WithToken(ChatGptSession saved, ChatGptToken token) => saved with
    {
        AccessToken = token.AccessToken, RefreshToken = token.RefreshToken, Scope = token.Scope,
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn)
    };

    private async Task<(string Subject, string? Email)> ValidateIdentityAsync(string? token, string client, string nonce, CancellationToken ct)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 3 })
            throw new InvalidDataException("ChatGPT did not provide an ID token.");
        using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
        if (Text(header.RootElement, "alg") != "RS256" || Text(header.RootElement, "kid") is not { } keyId)
            throw new InvalidDataException("Unsupported ChatGPT ID token signing key.");
        using var keys = JsonDocument.Parse(await http.GetByteArrayAsync(endpoints.Jwks, ct));
        var key = keys.RootElement.GetProperty("keys").EnumerateArray()
            .FirstOrDefault(x => Text(x, "kid") == keyId && Text(x, "kty") == "RSA"
                && Text(x, "use") is null or "sig" && Text(x, "alg") is null or "RS256");
        if (key.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("ChatGPT ID token signing key was not published.");

        using var rsa = RSA.Create(new RSAParameters
        {
            Modulus = Base64Url.DecodeFromChars(Text(key, "n")!),
            Exponent = Base64Url.DecodeFromChars(Text(key, "e")!)
        });
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!rsa.VerifyData(signed, Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Invalid ChatGPT ID token signature.");
        using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        var root = payload.RootElement;
        var audience = root.GetProperty("aud");
        var matches = audience.ValueKind == JsonValueKind.String ? audience.GetString() == client
            : audience.ValueKind == JsonValueKind.Array && audience.EnumerateArray().Any(a => a.GetString() == client)
                && (Text(root, "azp") ?? (audience.GetArrayLength() == 1 ? client : "")) == client;
        if (!matches || Text(root, "iss") != Issuer || Text(root, "nonce") != nonce
            || root.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            || Text(root, "sub") is not { Length: > 0 } subject)
            throw new InvalidDataException("ChatGPT ID token claims do not match this sign-in.");
        return (subject, Text(root, "email"));
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        string? code = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            code = ErrorCode(json.RootElement);
        }
        catch (JsonException) { }
        if (code is "invalid_grant" or "subscription_sharing_invalid_user" || response.StatusCode == HttpStatusCode.Unauthorized)
            throw SignInRequired();
        if (code == "subscription_sharing_usage_unavailable")
            throw new ChatGptException(code, "ChatGPT plan usage is unavailable for this account. Check ChatGPT Settings → Usage.");
        var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? $"; request {ids.FirstOrDefault()}" : "";
        throw new ChatGptException(code ?? "chatgpt_request_failed", $"ChatGPT returned HTTP {(int)response.StatusCode}{requestId}.");
    }

    internal static string? ErrorCode(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)
            ? error.ValueKind == JsonValueKind.String ? error.GetString() : Text(error, "code")
            : Text(root, "code");

    internal static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static ChatGptException SignInRequired() => new("chatgpt_sign_in_required", "Run mvec login to authorize ChatGPT plan usage.");
    private static string RandomValue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private FileStream LockSession()
    {
        try
        {
            return new FileStream(Path.Join(project.State, "chatgpt.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) { throw new IOException("Another mvec process is changing the ChatGPT connection; retry shortly.", ex); }
    }

    private ChatGptSession? ReadSession()
    {
        byte[] bytes;
        try
        {
            using var file = new FileStream(SessionPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
        }
        catch (FileNotFoundException) { return null; }
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize(bytes, Wire.Default.ChatGptSession);
    }

    private void WriteSession(ChatGptSession session)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session, Wire.Default.ChatGptSession);
        var temporary = $"{SessionPath}.{Guid.NewGuid():N}.new";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        else
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var file = new FileStream(temporary, options))
                file.Write(bytes);
            File.Move(temporary, SessionPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task<Callback> ReceiveAsync(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync(ct);
            try
            {
                using var input = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
                read.CancelAfter(TimeSpan.FromSeconds(5));
                var request = (await input.ReadLineAsync(read.Token))?.Split(' ');
                if (request is ["GET", var target, _] && target.StartsWith(CallbackPath + "?", StringComparison.Ordinal))
                {
                    var query = target[(CallbackPath.Length + 1)..].Split('&')
                        .Select(x => x.Split('=', 2)).ToLookup(x => WebUtility.UrlDecode(x[0]),
                            x => x.Length == 2 ? WebUtility.UrlDecode(x[1]) : "");
                    // Drain the headers before sending the redirect result.
                    while (await input.ReadLineAsync(read.Token) is { Length: > 0 }) { }
                    var callback = new Callback(client, query);
                    if (callback.Get("state") == state)
                        return callback;
                    await callback.ReplyAsync("400 Bad Request", "This callback does not match the pending sign-in.", read.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (IOException) { }
            catch
            {
                client.Dispose();
                throw;
            }
            client.Dispose();
        }
    }

    private sealed class Callback(TcpClient client, ILookup<string, string> query) : IDisposable
    {
        public string? Get(string field) => query[field].Count() == 1 ? query[field].First() : null;

        public async Task ReplyAsync(string status, string body, CancellationToken ct)
        {
            var message = $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                $"Cache-Control: no-store\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            try { await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(message), ct); }
            catch (IOException) { } // Closing the browser does not undo a completed exchange.
        }

        public void Dispose() => client.Dispose();
    }
}

internal sealed record ChatGptSession(string Host, string? ClientId = null, string? Subject = null, string? Email = null,
    string? AccessToken = null, string? RefreshToken = null, string? Scope = null, DateTimeOffset ExpiresAt = default);
internal sealed record ChatGptToken(string? AccessToken, string? RefreshToken, string? IdToken, string? Scope, string? TokenType, int ExpiresIn);
internal sealed class ChatGptException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
