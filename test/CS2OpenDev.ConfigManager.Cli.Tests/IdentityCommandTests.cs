using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli.Wizard;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     Backups signed for a Steam account through acb: <c>backup --sign</c> with a stand-in for the service, what
///     every command says about the signer, organiser policies that name Steam accounts, and the real client
///     (sign-in through a browser stand-in and a loopback port, then signing) against a stand-in service.
/// </summary>
public class IdentityCommandTests
{
    private const string Service = "https://identity.cs2opendev.test";

    [Test]
    public async Task BackupSign_WritesAnIdentityBackup_ThatEveryCommandNamesTheSignerOf()
    {
        using TestHost host = new() { IdentityService = Service };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("It is signed for Steam account " + TestIdentity.SteamId);

        using TestHost json = new();
        await Assert.That(json.Run("verify", "--json", zip)).IsEqualTo(ExitCodes.Done).Because(json.All);
        CommandReport report = JsonSerializer.Deserialize(json.Output, CliJson.Default.CommandReport)!;
        await Assert.That(report.Level).IsEqualTo("identity");
        await Assert.That(report.SignedBy).IsEqualTo(TestIdentity.SteamId);
        await Assert.That(json.Errors).Contains("Signed:   Steam account " + TestIdentity.SteamId);
        await Assert.That(json.Errors).DoesNotContain("the settings are account").Because("the signer's account is the one whose settings these are");
    }

    [Test]
    public async Task BackupSign_WithoutAService_OrWithSeal_IsRefused_AndWritesNothing()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("--identity-service");
        await Assert.That(host.Run("backup", "-o", zip, "--steam-path", source.Root, "--sign", "--identity-service", "http://identity.example")).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("is not https");
        await Assert.That(host.Run("backup", "-o", zip, "--steam-path", source.Root, "--sign", "--seal", "--identity-service", Service)).IsEqualTo(ExitCodes.Usage);
        await Assert.That(File.Exists(zip)).IsFalse();
        await Assert.That(host.IdentitySigner.Signed).IsEmpty();
    }

    [Test]
    public async Task Verify_ABackupSignedByAnotherAccount_SaysWhoseSettingsTheyAre()
    {
        using TestHost host = new() { IdentityService = Service, IdentitySigner = new TestIdentity.Signer("76561198000000001") };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Done).Because(host.All);

        await Assert.That(host.Run("verify", zip)).IsEqualTo(ExitCodes.Done);

        await Assert.That(host.Output).Contains($"the settings are account {FakeSteam.AccountId}'s; account 39734273 signed them");
    }

    /// <summary>A policy naming Steam accounts accepts a backup signed by one of them, and refuses one signed by anyone else.</summary>
    [Test]
    public async Task APolicyNamingSteamAccounts_AcceptsOnlyTheirSignatures()
    {
        using TestHost host = new() { IdentityService = Service };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Done).Because(host.All);
        string ours = host.Temp.Sub("ours.json");
        string theirs = host.Temp.Sub("theirs.json");
        File.WriteAllText(ours, $$"""{ "minimumLevel": "identity", "steamIds": ["{{TestIdentity.SteamId}}"] }""");
        File.WriteAllText(theirs, """{ "minimumLevel": "identity", "steamIds": ["76561198000000001"] }""");

        await Assert.That(host.Run("verify", zip, "--policy", ours)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Run("verify", zip, "--policy", theirs)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("this one is signed by " + TestIdentity.SteamId);
    }

    [Test]
    public async Task TheBackupScreen_SignsWhenAServiceIsSet_AndSaysWhyNotOtherwise()
    {
        using TestHost host = new() { IdentityService = Service };
        using FakeSteam steam = new();
        BackupFlow flow = new(host.Host()) { SteamPath = steam.Root, OutputPath = host.Temp.Sub("b.zip"), Sign = true };
        await Assert.That(flow.Look()).IsTrue();

        await Assert.That(flow.Save()).IsEqualTo(ExitCodes.Done).Because(flow.SaveProblem ?? "");
        await Assert.That(flow.Signer).IsEqualTo(TestIdentity.SteamId);
        await Assert.That(string.Join('\n', flow.DoneLines())).Contains("Signed for Steam account " + TestIdentity.SteamId);

        using TestHost none = new();
        BackupFlow unsigned = new(none.Host());
        await Assert.That(unsigned.SignProblem).Contains("needs the CS2OpenDev service");
    }

    [Test]
    [Arguments("https://identity.cs2opendev.dev", null)]
    [Arguments("http://127.0.0.1:5000", null)]
    [Arguments("http://identity.cs2opendev.dev", "is not https")]
    [Arguments("https://user@identity.cs2opendev.dev", "not a service address")]
    [Arguments("identity.cs2opendev.dev", "not a service address")]
    public async Task OnlyHttps_OrLoopbackForTesting_IsAServiceAddress(string service, string? expected)
    {
        string? problem = ServiceSigner.Problem(service);

        if (expected is null)
        {
            await Assert.That(problem).IsNull();
        }
        else
        {
            await Assert.That(problem).Contains(expected);
        }
    }

    /// <summary>
    ///     The real client against a stand-in service and identity provider: it signs in with PKCE through the browser
    ///     stand-in, which comes back to the loopback port (once with another sign-in's state, which is ignored), swaps
    ///     the code for a token, and sends only the manifest's digest to be signed.
    /// </summary>
    [Test]
    public async Task TheClient_SignsInThroughTheBrowser_AndSendsOnlyTheDigest()
    {
        StubService stub = new();
        using HttpClient http = new(stub);
        using HttpClient browserHttp = new();
        List<Task> visits = [];
        bool OpenBrowser(string url)
        {
            Dictionary<string, string> query = Query(new Uri(url));
            stub.Challenge = query["code_challenge"];
            string back = query["redirect_uri"];
            visits.Add(Task.Run(async () =>
            {
                using HttpResponseMessage stray = await browserHttp.GetAsync(back + "?code=stray&state=someone-else");
                using HttpResponseMessage ok = await browserHttp.GetAsync(back + "?code=the-code&state=" + Uri.EscapeDataString(query["state"]));
                stub.CallbackPage = await ok.Content.ReadAsStringAsync();
            }));
            return true;
        }

        StringWriter words = new();
        ServiceSigner signer = new(Service, http, OpenBrowser, words);
        string digest = new('c', 64);

        DsseEnvelope envelope = signer.Sign(digest, Platforms.WindowsX64, new ArchiveTool("acb", "test"));
        await Task.WhenAll(visits);

        await Assert.That(stub.TokenForm).Contains("code=the-code");
        await Assert.That(stub.VerifierMatched).IsTrue().Because("the token request carries the verifier the challenge was made from");
        await Assert.That(stub.Bearer).IsEqualTo("token-123");
        await Assert.That(stub.SignBody).IsEqualTo($$$"""{"manifestSha256":"{{{digest}}}","platform":"windows-x64","tool":{"name":"acb","version":"test"}}""");
        await Assert.That(stub.CallbackPage).Contains("Signed in");
        await Assert.That(words.ToString()).Contains("Signing in with Steam in your browser");
        await Assert.That(envelope.Signatures.Count).IsEqualTo(1);

        signer.Sign(digest, Platforms.WindowsX64, new ArchiveTool("acb", "test"));
        await Assert.That(stub.ConfigCalls).IsEqualTo(1).Because("one sign-in serves every signature this run makes");
    }

    [Test]
    public async Task TheClient_SaysWhenSignInWasRefused()
    {
        StubService stub = new();
        using HttpClient http = new(stub);
        using HttpClient browserHttp = new();
        Task? visit = null;
        ServiceSigner signer = new(Service, http, url =>
        {
            Dictionary<string, string> query = Query(new Uri(url));
            visit = Task.Run(async () => (await browserHttp.GetAsync(query["redirect_uri"] + "?error=access_denied&state=" + Uri.EscapeDataString(query["state"]))).Dispose());
            return true;
        }, TextWriter.Null);

        await Assert.That(() => signer.Sign(new string('c', 64), Platforms.WindowsX64, new ArchiveTool("acb", "test")))
            .Throws<IdentityServiceException>().WithMessageContaining("access_denied");
        await visit!;
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    /// <summary>The service and its identity provider, answering as docs/identity-service.md says, signing with the test keys.</summary>
    private sealed class StubService : HttpMessageHandler
    {
        public string? Challenge { get; set; }

        public string TokenForm { get; private set; } = "";

        public bool VerifierMatched { get; private set; }

        public string? Bearer { get; private set; }

        public string SignBody { get; private set; } = "";

        public string CallbackPage { get; set; } = "";

        public int ConfigCalls { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "";
            switch (path)
            {
                case "/v1/acb/config":
                    ConfigCalls++;
                    return Json("""{ "issuer": "https://login.cs2opendev.test/realms/cs2opendev", "clientId": "acb", "scope": "openid" }""");
                case "/realms/cs2opendev/.well-known/openid-configuration":
                    return Json("""{ "authorization_endpoint": "https://login.cs2opendev.test/auth", "token_endpoint": "https://login.cs2opendev.test/token", "issuer": "x" }""");
                case "/token":
                    TokenForm = body;
                    string verifier = Uri.UnescapeDataString(body.Split('&').Single(p => p.StartsWith("code_verifier=", StringComparison.Ordinal))["code_verifier=".Length..]);
                    VerifierMatched = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_') == Challenge;
                    return Json("""{ "access_token": "token-123", "token_type": "Bearer", "expires_in": 300 }""");
                case "/v1/acb/sign":
                    Bearer = request.Headers.Authorization?.Parameter;
                    SignBody = body;
                    string digest = JsonDocument.Parse(body).RootElement.GetProperty("manifestSha256").GetString()!;
                    return Json(IdentityIssuer.ToJson(new TestIdentity.Signer().Sign(digest, "", new ArchiveTool("", ""))));
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
