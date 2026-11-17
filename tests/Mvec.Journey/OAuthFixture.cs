using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Mvec;

internal sealed class OAuthFixture : IDisposable
{
    public const string Model = "summary-model-from-toml";
    private const string Client = "oaiapp_journey";
    private readonly RSA rsa = RSA.Create(2048);
    private string nonce = "";
    private string challenge = "";
    private string redirect = "";
    public ChatGptEndpoints Endpoints { get; private set; } = null!;
    public int Refreshes { get; private set; }
    public int Summaries { get; private set; }
    public int Revocations { get; private set; }
    public bool Interrupt { get; set; }

    public void ListenAt(string baseUrl) => Endpoints = new(baseUrl + "/authorize", baseUrl + "/token", baseUrl + "/jwks",
        baseUrl + "/responses", baseUrl + "/models", baseUrl + "/revoke");

    public async Task SignInAsync(ChatGptOAuth oauth, CancellationToken ct)
    {
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = oauth.SignInAsync(0, uri => opened.SetResult(uri), ct);
        var uri = await opened.Task.WaitAsync(ct);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Require(query["client_id"] == "dynamic_agent_client" && query["agent_name_hint"] == "mvec"
            && query["ext_agent_host_id"].ToString().StartsWith("urn:uuid:", StringComparison.Ordinal)
            && query["code_challenge_method"] == "S256" && query["resource"] == ChatGptOAuth.Resource
            && query["scope"].ToString().Split(' ').Contains(ChatGptOAuth.PlanScope), "dynamic registration requests plan usage");
        nonce = query["nonce"].ToString();
        challenge = query["code_challenge"].ToString();
        redirect = query["redirect_uri"].ToString();
        using var browser = new HttpClient();
        using var bad = await browser.GetAsync(redirect + "?state=wrong&code=ignored", ct);
        Require(bad.StatusCode == HttpStatusCode.BadRequest, "wrong browser state rejected");
        using var callback = await browser.GetAsync($"{redirect}?state={query["state"]}&code=fixture-code&client_id={Client}", ct);
        Require(callback.IsSuccessStatusCode && await pending == "klee@example.invalid", "browser authorization and validated identity");
    }

    public void Map(WebApplication api)
    {
        api.MapGet("/jwks", () =>
        {
            var key = rsa.ExportParameters(false);
            return new { keys = new[] { new { kid = "journey", kty = "RSA", alg = "RS256",
                n = Base64Url.EncodeToString(key.Modulus!), e = Base64Url.EncodeToString(key.Exponent!) } } };
        });
        api.MapPost("/token", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            Require(form["client_id"] == Client && form["resource"] == ChatGptOAuth.Resource, "issued client and resource on exchanges");
            var refresh = form["grant_type"] == "refresh_token";
            if (refresh)
            {
                Require(form["refresh_token"] == "refresh-initial", "refresh uses the original renewable grant");
                Refreshes++;
            }
            else
            {
                var verifier = Encoding.ASCII.GetBytes(form["code_verifier"].ToString());
                Require(form["code"] == "fixture-code" && form["redirect_uri"] == redirect
                    && Base64Url.EncodeToString(SHA256.HashData(verifier)) == challenge, "PKCE and original callback URI at exchange");
            }
            return Results.Json(new
            {
                access_token = refresh ? "access-rotated" : "access-initial",
                refresh_token = refresh ? "refresh-rotated" : "refresh-initial",
                id_token = refresh ? null : Identity(),
                scope = "openid offline_access resource.invoke " + ChatGptOAuth.PlanScope,
                token_type = "Bearer", expires_in = refresh ? 3600 : 30
            });
        });
        api.MapGet("/models", (HttpContext context) =>
        {
            Require(context.Request.Headers.Authorization == "Bearer access-rotated", "model list uses the saved bearer");
            return new { models = new[] { new { slug = Model, display_name = "Fixture Summary Model", visibility = "list" } } };
        });
        api.MapPost("/responses", async context =>
        {
            Require(context.Request.Headers.Authorization == "Bearer access-rotated", "Responses uses the rotated OAuth token");
            using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var request = json.RootElement;
            Require(request.GetProperty("model").GetString() == Model && request.GetProperty("stream").GetBoolean()
                && !request.GetProperty("store").GetBoolean() && request.GetProperty("input")[0].GetProperty("role").GetString() == "user",
                "configured model and required Responses streaming fields");
            Summaries++;
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"delta\":\"A concise \"}\n\n"
                + "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OAuth summary.\"}\n\n", context.RequestAborted);
            if (!Interrupt)
                await context.Response.WriteAsync("data: {\"type\":\"response.completed\"}\n\n", context.RequestAborted);
        });
        api.MapPost("/revoke", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            Require(form["token"] == "refresh-rotated" && form["client_id"] == Client, "revocation keeps rotated token and issued client paired");
            Revocations++;
            return Results.Ok();
        });
    }

    private string Identity()
    {
        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "journey" }));
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = Client, iss = ChatGptOAuth.Issuer, sub = "klee-journey",
            exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), nonce, email = "klee@example.invalid"
        }));
        var data = header + "." + payload;
        return data + "." + Base64Url.EncodeToString(rsa.SignData(Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static void Require(bool condition, string check)
    {
        if (!condition)
            throw new InvalidOperationException($"OAuth journey failed: {check}");
    }

    public void Dispose() => rsa.Dispose();
}
