using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2OpenDev.ConfigManager.Archive;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>Why the CS2OpenDev service did not sign. An <see cref="IOException" />, so a backup that needed it is not written.</summary>
internal sealed class IdentityServiceException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>What the service says about itself: where to sign in, and as which client. <c>GET /v1/acb/config</c>.</summary>
internal sealed record ServiceConfig(string Issuer, string ClientId, string Scope);

/// <summary>The identity provider's OpenID discovery document, the two endpoints acb uses.</summary>
internal sealed record OidcDiscovery(
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint);

internal sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

/// <summary>What is sent to be signed: the manifest's digest, the platform and the tool. Never the settings.</summary>
internal sealed record SignRequest(string ManifestSha256, string Platform, ArchiveTool Tool);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServiceConfig))]
[JsonSerializable(typeof(OidcDiscovery))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(SignRequest))]
internal sealed partial class ServiceJson : JsonSerializerContext;

/// <summary>
///     Signs a manifest through the CS2OpenDev service (docs/identity-service.md). The first signature signs the
///     person in: the service names its identity provider, which brokers Steam, and acb runs the OAuth authorization
///     code flow with PKCE through the browser, receiving the code on a loopback port. The token lives only in this
///     process. Only the manifest's SHA-256, the platform and the tool are sent.
/// </summary>
internal sealed class ServiceSigner : IIdentitySigner
{
    /// <summary>How long the person has to sign in in the browser.</summary>
    internal static readonly TimeSpan SignInLimit = TimeSpan.FromMinutes(5);

    private readonly Uri _service;
    private readonly HttpClient _http;
    private readonly Func<string, bool> _openBrowser;
    private readonly TextWriter _words;
    private string? _token;

    public ServiceSigner(string service, HttpClient http, Func<string, bool> openBrowser, TextWriter words)
    {
        _service = Problem(service) is { } problem ? throw new IdentityServiceException(problem) : new Uri(service.TrimEnd('/') + "/");
        _http = http;
        _openBrowser = openBrowser;
        _words = words;
    }

    /// <summary>Why <paramref name="service" /> is not a service address acb uses: https only, except a loopback one for testing.</summary>
    public static string? Problem(string? service)
    {
        if (!Uri.TryCreate(service, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0)
        {
            return $"'{service}' is not a service address (https://...)";
        }

        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            ? null
            : $"'{service}' is not https, and a token for your Steam account is sent to it";
    }

    public DsseEnvelope Sign(string manifestSha256, string platform, ArchiveTool tool)
    {
        _token ??= SignIn();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(_service, "v1/acb/sign"))
        {
            Content = JsonContent.Create(new SignRequest(manifestSha256, platform, tool), ServiceJson.Default.SignRequest)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        string body = Send(request, "sign the backup");
        try
        {
            return IdentityIssuer.FromJson(body);
        }
        catch (JsonException ex)
        {
            throw new IdentityServiceException("The service's answer is not a signature acb reads: " + ex.Message, ex);
        }
    }

    /// <summary>Signs in through the browser and returns an access token for the service.</summary>
    private string SignIn()
    {
        ServiceConfig config = Get(new Uri(_service, "v1/acb/config"), ServiceJson.Default.ServiceConfig, "ask the service where to sign in");
        if (Problem(config.Issuer) is { } badIssuer)
        {
            throw new IdentityServiceException("The service names a sign-in address acb will not use: " + badIssuer + ".");
        }

        OidcDiscovery discovery = Get(new Uri(config.Issuer.TrimEnd('/') + "/.well-known/openid-configuration"), ServiceJson.Default.OidcDiscovery,
            "reach the sign-in service");
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        string redirect = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback");
        string authorize = discovery.AuthorizationEndpoint + (discovery.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?")
                           + Query(("response_type", "code"), ("client_id", config.ClientId), ("redirect_uri", redirect), ("scope", config.Scope),
                               ("state", state), ("code_challenge", challenge), ("code_challenge_method", "S256"));
        _words.WriteLine("Signing in with Steam in your browser. If it does not open, go to:");
        _words.WriteLine("  " + authorize);
        _openBrowser(authorize);

        string code = AwaitCallback(listener, state);
        using HttpRequestMessage request = new(HttpMethod.Post, discovery.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirect), new("client_id", config.ClientId),
                new("code_verifier", verifier)
            ])
        };
        return Parse(Send(request, "finish signing in"), ServiceJson.Default.TokenResponse, "finish signing in").AccessToken;
    }

    /// <summary>
    ///     Waits for the browser to come back to the loopback port with the code, and answers it with a page saying
    ///     the tab can close. A request that does not carry this sign-in's state is answered and ignored.
    /// </summary>
    private static string AwaitCallback(TcpListener listener, string state)
    {
        DateTime until = DateTime.UtcNow + SignInLimit;
        while (DateTime.UtcNow < until)
        {
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            if (!accept.Wait(until - DateTime.UtcNow))
            {
                break;
            }

            using TcpClient client = accept.Result;
            client.ReceiveTimeout = 10_000;
            using NetworkStream stream = client.GetStream();
            string? target = RequestTarget(stream);
            Dictionary<string, string> query = target is null ? [] : ParseQuery(target);
            bool ours = target is not null && target.StartsWith("/callback?", StringComparison.Ordinal) && query.GetValueOrDefault("state") == state;
            string page = !ours ? "Not a sign-in for acb." : query.ContainsKey("error") ? "Sign-in did not finish. You can close this tab." : "Signed in. You can close this tab and go back to acb.";
            byte[] html = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>acb</title><p>{WebUtility.HtmlEncode(page)}</p>");
            byte[] head = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"HTTP/1.1 {(ours ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n"));
            stream.Write(head);
            stream.Write(html);
            if (!ours)
            {
                continue;
            }

            if (query.TryGetValue("error", out string? error))
            {
                throw new IdentityServiceException("Sign-in did not finish: " + error + ".");
            }

            return query.TryGetValue("code", out string? code) && code.Length > 0
                ? code
                : throw new IdentityServiceException("The sign-in came back without a code.");
        }

        throw new IdentityServiceException($"Nobody signed in within {SignInLimit.TotalMinutes} minutes.");
    }

    /// <summary>The target of a request's first line (<c>GET /callback?... HTTP/1.1</c>), read up to the end of its headers; null for anything else.</summary>
    private static string? RequestTarget(NetworkStream stream)
    {
        byte[] buffer = new byte[8192];
        int length = 0;
        try
        {
            while (length < buffer.Length)
            {
                int n = stream.Read(buffer, length, buffer.Length - length);
                if (n == 0)
                {
                    break;
                }

                length += n;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }

        string[] line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
        return line is ["GET", { } target, ['H', 'T', 'T', 'P', ..]] ? target : null;
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        Dictionary<string, string> query = new(StringComparer.Ordinal);
        int at = target.IndexOf('?', StringComparison.Ordinal);
        foreach (string pair in at < 0 ? [] : target[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            query.TryAdd(key, eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')));
        }

        return query;
    }

    private T Get<T>(Uri uri, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string doing)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        return Parse(Send(request, doing), type, doing);
    }

    private string Send(HttpRequestMessage request, string doing)
    {
        try
        {
            using HttpResponseMessage response = _http.Send(request);
            using StreamReader reader = new(response.Content.ReadAsStream(), Encoding.UTF8);
            string body = reader.ReadToEnd();
            return response.IsSuccessStatusCode
                ? body
                : throw new IdentityServiceException($"acb could not {doing}: the service answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (HttpRequestException ex)
        {
            throw new IdentityServiceException($"acb could not {doing}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new IdentityServiceException($"acb could not {doing}: the service did not answer in time.", ex);
        }
    }

    private static T Parse<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string doing)
    {
        try
        {
            return JsonSerializer.Deserialize(body, type) ?? throw new IdentityServiceException($"acb could not {doing}: the answer was empty.");
        }
        catch (JsonException ex)
        {
            throw new IdentityServiceException($"acb could not {doing}: the answer cannot be read ({ex.Message}).", ex);
        }
    }

    private static string Query(params (string Key, string Value)[] pairs) =>
        string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
