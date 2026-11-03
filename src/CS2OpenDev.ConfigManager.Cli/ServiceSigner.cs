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

/// <summary>
///     The CS2OpenDev service this build signs through, and the identity provider it signs in at. A token for the
///     person's Steam account goes only to this service, so a service named on the command line or in the
///     environment cannot relay it. No other service is taken, not even one on this PC: a stand-in there could
///     send the browser on to the real provider and swap the code it gets back for a real token. Tests put their
///     stand-in here instead.
/// </summary>
internal sealed record IdentityServiceAddress(string Service, string Issuer)
{
    /// <summary>None yet: the service is not running. The release that ships the root keys sets this beside them.</summary>
    public static IdentityServiceAddress? Shipped => null;

    /// <summary>What every run uses: <see cref="Shipped" />, which only a test assembly replaces.</summary>
    internal static IdentityServiceAddress? Current { get; set; } = Shipped;
}

/// <summary>What the service says about itself: where to sign in, and as which client. <c>GET /v1/acb/config</c>.</summary>
internal sealed record ServiceConfig(string Issuer, string ClientId, string Scope);

/// <summary>The identity provider's OpenID discovery document: its issuer and the two endpoints acb uses.</summary>
internal sealed record OidcDiscovery(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint);

internal sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

/// <summary>What is sent to be signed: the manifest's digest, the platform and the tool. Never the settings.</summary>
internal sealed record SignRequest(string ManifestSha256, string Platform, ArchiveTool Tool);

/// <summary>A member missing from an answer, or null, is refused as an answer acb cannot read, never read as null.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ServiceConfig))]
[JsonSerializable(typeof(OidcDiscovery))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(SignRequest))]
internal sealed partial class ServiceJson : JsonSerializerContext;

/// <summary>
///     Signs a manifest through the CS2OpenDev service (docs/identity-service.md). The first signature signs the
///     person in: the service names its identity provider, which brokers Steam, and acb runs the OAuth authorization
///     code flow with PKCE through the browser, receiving the code on a loopback port. The token lives only in this
///     process and goes only to <see cref="IdentityServiceAddress.Current" />. Only the manifest's SHA-256, the
///     platform and the tool are sent.
/// </summary>
internal sealed class ServiceSigner : IIdentitySigner
{
    /// <summary>How long the person has to sign in in the browser.</summary>
    internal static readonly TimeSpan SignInLimit = TimeSpan.FromMinutes(5);

    /// <summary>How long one connection to the loopback port has to send its whole request, in milliseconds.</summary>
    private const long RequestLimitMs = 10_000;

    private readonly Uri _service;
    private readonly HttpClient _http;
    private readonly Func<string, bool> _openBrowser;
    private readonly TextWriter _words;
    private readonly CancellationToken _cancel;
    private readonly TimeSpan _signInLimit;
    private string? _token;

    public ServiceSigner(string service, HttpClient http, Func<string, bool> openBrowser, TextWriter words, TimeSpan? signInLimit = null,
        CancellationToken cancel = default)
    {
        _service = Problem(service) is { } problem ? throw new IdentityServiceException(problem) : new Uri(service.TrimEnd('/') + "/");
        _http = http;
        _openBrowser = openBrowser;
        _words = words;
        _cancel = cancel;
        _signInLimit = signInLimit ?? SignInLimit;
    }

    /// <summary>
    ///     Why <paramref name="service" /> is not a service acb signs through: it must be
    ///     <see cref="IdentityServiceAddress.Current" />, whatever address it is on.
    /// </summary>
    public static string? Problem(string? service)
    {
        if (AddressProblem(service) is { } bad)
        {
            return bad;
        }

        Uri uri = new(service!.TrimEnd('/') + "/");
        return IdentityServiceAddress.Current is not { } known
            ? "this acb does not know the CS2OpenDev service's address yet, and it signs through no other"
            : uri == new Uri(known.Service.TrimEnd('/') + "/")
                ? null
                : $"'{service}' is not the CS2OpenDev service ({known.Service}), the only one this acb signs through: a token for your Steam account would be sent to it";
    }

    /// <summary>Why <paramref name="address" /> is not one acb sends anything to: https only, except a test's stand-in on loopback.</summary>
    private static string? AddressProblem(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return $"'{address}' is not a service address (https://...)";
        }

        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            ? null
            : $"'{address}' is not https, and a token for your Steam account is sent to it";
    }

    public DsseEnvelope Sign(string manifestSha256, string platform, ArchiveTool tool)
    {
        try
        {
            _token ??= SignIn();
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(_service, "v1/acb/sign"))
            {
                Content = JsonContent.Create(new SignRequest(manifestSha256, platform, tool), ServiceJson.Default.SignRequest)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            string body = Send(request, "sign the backup", signing: true);
            try
            {
                return Dsse.FromJson(body);
            }
            catch (JsonException ex)
            {
                throw new IdentityServiceException("The service's answer is not a signature acb reads: " + ex.Message, ex);
            }
        }
        catch (OperationCanceledException ex) when (_cancel.IsCancellationRequested)
        {
            throw new IdentityServiceException("Signing in was cancelled.", ex);
        }
    }

    /// <summary>
    ///     Signs in through the browser and returns an access token for the service. Every address the service and
    ///     its provider name is checked before anything is opened or sent to it.
    /// </summary>
    private string SignIn()
    {
        ServiceConfig config = Get(new Uri(_service, "v1/acb/config"), ServiceJson.Default.ServiceConfig, "ask the service where to sign in");
        if (IssuerProblem(config.Issuer) is { } badIssuer)
        {
            throw new IdentityServiceException("The service names a sign-in address acb will not use: " + badIssuer + ".");
        }

        Uri issuer = new(config.Issuer);
        OidcDiscovery discovery = Get(new Uri(config.Issuer.TrimEnd('/') + "/.well-known/openid-configuration"), ServiceJson.Default.OidcDiscovery,
            "reach the sign-in service");
        if (discovery.Issuer != config.Issuer)
        {
            throw new IdentityServiceException($"The sign-in service says it is '{discovery.Issuer}', not '{config.Issuer}', so acb does not sign in there.");
        }

        Uri authorization = Endpoint(discovery.AuthorizationEndpoint, issuer, "authorization_endpoint");
        Uri tokenEndpoint = Endpoint(discovery.TokenEndpoint, issuer, "token_endpoint");
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using TcpListener listener = new(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new IdentityServiceException("acb could not listen on this PC's loopback address for the sign-in: " + ex.Message, ex);
        }

        string redirect = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback");
        string authorize = authorization.AbsoluteUri + (authorization.Query.Length > 0 ? "&" : "?")
                           + Query(("response_type", "code"), ("client_id", config.ClientId), ("redirect_uri", redirect), ("scope", config.Scope),
                               ("state", state), ("code_challenge", challenge), ("code_challenge_method", "S256"));
        _words.WriteLine($"Signing in to the CS2OpenDev service at {_service.Authority} with Steam, in your browser. If it does not open, open this address in a browser on this PC:");
        _words.WriteLine("  " + authorize);
        if (!_openBrowser(authorize))
        {
            _words.WriteLine("acb could not open a browser here; open the address above in one yourself, on this PC: the sign-in comes back to it.");
        }

        string code = AwaitCallback(listener, state);
        using HttpRequestMessage request = new(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirect), new("client_id", config.ClientId),
                new("code_verifier", verifier)
            ])
        };
        return Parse(Send(request, "finish signing in"), ServiceJson.Default.TokenResponse, "finish signing in").AccessToken;
    }

    /// <summary>Why the service's issuer is not one acb signs in at: the known service must name the known issuer.</summary>
    private static string? IssuerProblem(string issuer)
    {
        if (AddressProblem(issuer) is { } bad)
        {
            return bad;
        }

        return IdentityServiceAddress.Current is { } known && issuer == known.Issuer
            ? null
            : $"'{issuer}' is not the sign-in service this acb knows";
    }

    /// <summary>An endpoint from the discovery document, which must be on the issuer's own scheme, host and port.</summary>
    private static Uri Endpoint(string endpoint, Uri issuer, string name)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
            || Uri.Compare(uri, issuer, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new IdentityServiceException($"The sign-in service's {name} '{endpoint}' is not on {issuer.GetLeftPart(UriPartial.Authority)}, so acb does not use it.");
        }

        return uri;
    }

    /// <summary>
    ///     Waits for the browser to come back to the loopback port with the code, and answers it with a page saying
    ///     the tab can close. A request that does not carry this sign-in's state is answered and ignored. Each
    ///     connection has one deadline for its whole request, never one per read, and Cancel and the sign-in limit
    ///     end it mid-read, so another program on this PC that connects and sends a byte now and then holds nothing.
    ///     Connections are read one at a time, so such a program can still keep the browser's callback waiting until
    ///     the limit; that only ends the sign-in, and a program on this PC could as well end acb itself.
    /// </summary>
    private string AwaitCallback(TcpListener listener, string state)
    {
        long until = Environment.TickCount64 + (long)_signInLimit.TotalMilliseconds;
        for (long left = until - Environment.TickCount64; left > 0; left = until - Environment.TickCount64)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancel);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(left));
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClientAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!_cancel.IsCancellationRequested)
            {
                break;
            }

            using (client)
            {
                // The time left is taken again after the accept, which may have waited most of it.
                deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(until - Environment.TickCount64, 1, RequestLimitMs)));
                if (Answer(client, state, deadline.Token) is { } code)
                {
                    return code;
                }
            }
        }

        throw new IdentityServiceException(string.Create(CultureInfo.InvariantCulture,
            $"Nobody signed in within {_signInLimit.TotalMinutes} minutes. The sign-in comes back to this PC, so it has to be done in a browser on this PC."));
    }

    /// <summary>
    ///     Reads one request and answers it: the code when it is this sign-in's callback, null for anything else,
    ///     including a request not sent by <paramref name="deadline" />.
    /// </summary>
    private string? Answer(TcpClient client, string state, CancellationToken deadline)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            string? target = RequestTarget(stream, deadline);
            Dictionary<string, string> query = target is null ? [] : ParseQuery(target);
            bool ours = target is not null && target.StartsWith("/callback?", StringComparison.Ordinal) && query.GetValueOrDefault("state") == state;
            string page = !ours ? "Not a sign-in for acb." : query.ContainsKey("error") ? "Sign-in did not finish. You can close this tab." : "Signed in. You can close this tab and go back to acb.";
            byte[] html = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>acb</title><p>{WebUtility.HtmlEncode(page)}</p>");
            byte[] head = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"HTTP/1.1 {(ours ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n"));
            try
            {
                stream.WriteAsync(head, deadline).AsTask().GetAwaiter().GetResult();
                stream.WriteAsync(html, deadline).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ours && (ex is IOException || (ex is OperationCanceledException && !_cancel.IsCancellationRequested)))
            {
                // The browser went away before the page; the code it brought still counts.
            }

            if (!ours)
            {
                return null;
            }

            if (query.TryGetValue("error", out string? error))
            {
                throw new IdentityServiceException("Sign-in did not finish: " + error + ".");
            }

            return query.TryGetValue("code", out string? code) && code.Length > 0
                ? code
                : throw new IdentityServiceException("The sign-in came back without a code.");
        }
        catch (Exception ex) when ((ex is IOException and not IdentityServiceException) || (ex is OperationCanceledException && !_cancel.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>The target of a request's first line (<c>GET /callback?... HTTP/1.1</c>), read up to the end of its headers; null for anything else.</summary>
    private static string? RequestTarget(NetworkStream stream, CancellationToken deadline)
    {
        byte[] buffer = new byte[8192];
        int length = 0;
        try
        {
            while (length < buffer.Length)
            {
                int n = stream.ReadAsync(buffer.AsMemory(length), deadline).AsTask().GetAwaiter().GetResult();
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

    /// <summary>
    ///     What a failure status means, in the contract's terms, with the status for anyone reporting it. Only the
    ///     status and Retry-After are read, never the body. The contract gives 403 and 429 an account meaning only for
    ///     the sign request, so on any other step they get plain words.
    /// </summary>
    internal static string Refusal(HttpResponseMessage response, bool signing)
    {
        int status = (int)response.StatusCode;
        string answered = string.Create(CultureInfo.InvariantCulture, $"(the service answered {status} {response.ReasonPhrase})");
        return status switch
        {
            401 => $"the service did not accept the sign-in, which may have expired; run the backup again to sign in afresh {answered}.",
            403 when signing => $"the service does not let this Steam account sign backups {answered}.",
            429 when signing => $"the service has signed too many backups for this account; try again {RetryAfter(response)} {answered}.",
            403 => $"the request was refused {answered}.",
            429 => $"the service is busy; try again {RetryAfter(response)} {answered}.",
            >= 500 and < 600 => $"the service cannot sign right now; try again later {answered}.",
            _ => $"the service answered {status} {response.ReasonPhrase}."
        };
    }

    /// <summary>When the service says to try again, from Retry-After: "in N minutes", else "later".</summary>
    private static string RetryAfter(HttpResponseMessage response)
    {
        TimeSpan? wait = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null
        };
        return wait is { } w && w > TimeSpan.Zero
            ? string.Create(CultureInfo.InvariantCulture, $"in {Math.Max(1, (int)Math.Ceiling(w.TotalMinutes))} minute{(w.TotalMinutes > 1 ? "s" : "")}")
            : "later";
    }

    private string Send(HttpRequestMessage request, string doing, bool signing = false)
    {
        try
        {
            using HttpResponseMessage response = _http.Send(request, _cancel);
            using StreamReader reader = new(response.Content.ReadAsStream(), Encoding.UTF8);
            string body = reader.ReadToEnd();
            return response.IsSuccessStatusCode ? body : throw new IdentityServiceException($"acb could not {doing}: {Refusal(response, signing)}");
        }
        catch (HttpRequestException ex)
        {
            throw new IdentityServiceException($"acb could not {doing}: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!_cancel.IsCancellationRequested)
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
