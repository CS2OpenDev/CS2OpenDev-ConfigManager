using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli.Wizard;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     Backups signed for a Steam account through acb: <c>backup --sign</c> with a stand-in for the service, what
///     every command says about the signer, organiser policies that name Steam accounts, and the real client
///     (sign-in through a browser stand-in and a loopback port, then signing) against a stand-in service.
/// </summary>
public class IdentityCommandTests
{
    /// <summary>The service and identity provider this test assembly's acb knows, in place of the shipped ones (none yet).</summary>
    internal const string Service = "https://identity.cs2opendev.test";

    private const string Issuer = "https://login.cs2opendev.test/realms/cs2opendev";

    [ModuleInitializer]
    internal static void Install() => IdentityServiceAddress.Current = new IdentityServiceAddress(Service, Issuer);

    [Test]
    public async Task BackupSign_WritesAnIdentityBackup_ThatEveryCommandNamesTheSignerOf()
    {
        using TestHost host = new() { IdentityService = Service, Interactive = true };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");
        host.Answers.Enqueue("y");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.All).Contains("the settings name no Steam account to check that against").Because("the game's own settings name no account");
        await Assert.That(host.Output).Contains($"It is signed for Steam account (SteamID64 {TestIdentity.SteamId})");

        using TestHost json = new();
        await Assert.That(json.Run("verify", "--json", zip)).IsEqualTo(ExitCodes.Done).Because(json.All);
        CommandReport report = JsonSerializer.Deserialize(json.Output, CliJson.Default.CommandReport)!;
        await Assert.That(report.Level).IsEqualTo("identity");
        await Assert.That(report.SignedBy).IsEqualTo(TestIdentity.SteamId);
        await Assert.That(json.Errors).Contains($"Signed:   Steam account (SteamID64 {TestIdentity.SteamId})");
        await Assert.That(json.Errors).Contains("checked against the keys revoked up to 2026-11-01");
        await Assert.That(report.RevokedAsOf).IsEqualTo(TestIdentity.RevokedAsOf);
        await Assert.That(json.Errors).DoesNotContain(Archives.CarriedRestorer).Because("this backup carries no restorer");
        await Assert.That(json.Errors).DoesNotContain("the settings are from").Because("the signer's account is the one whose settings these are");
    }

    [Test]
    public async Task BackupSign_WithoutAService_OrWithSeal_OrWithNobodyAtTheTerminal_IsRefused_AndWritesNothing()
    {
        using TestHost host = new() { Interactive = true };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("does not know the CS2OpenDev service's address yet");
        await Assert.That(host.Run("backup", "-o", zip, "--steam-path", source.Root, "--sign", "--identity-service", "http://identity.example")).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("is not https");
        await Assert.That(host.Run("backup", "-o", zip, "--steam-path", source.Root, "--sign", "--identity-service", "https://relay.example")).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("is not the CS2OpenDev service");
        await Assert.That(host.Run("backup", "-o", zip, "--steam-path", source.Root, "--sign", "--identity-service", "http://127.0.0.1:9000")).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("is not the CS2OpenDev service");
        await Assert.That(host.Run("backup", "-o", zip, "--steam-path", source.Root, "--sign", "--seal", "--identity-service", Service)).IsEqualTo(ExitCodes.Usage);

        using TestHost headless = new() { IdentityService = Service };
        await Assert.That(headless.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Refused);
        await Assert.That(headless.Output).Contains("runs only at a terminal");

        await Assert.That(File.Exists(zip)).IsFalse();
        await Assert.That(host.IdentitySigner.Signed).IsEmpty();
        await Assert.That(headless.IdentitySigner.Signed).IsEmpty();
    }

    [Test]
    public async Task Verify_ABackupSignedByAnotherAccount_SaysWhoseSettingsTheyAre()
    {
        using TestHost host = new() { IdentityService = Service, Interactive = true, IdentitySigner = new TestIdentity.Signer("76561198000000001") };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");
        host.Answers.Enqueue("y");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Done).Because(host.All);

        await Assert.That(host.Run("verify", zip)).IsEqualTo(ExitCodes.Done);

        await Assert.That(host.Output).Contains($"the settings are from {Identity.Account(FakeSteam.AccountId)}; account 39734273 (SteamID64 76561198000000001) signed them");

        using FakeSteam target = new();
        RestoreFlow flow = new(host.Host()) { ArchivePath = zip, SteamPath = target.Root };
        await Assert.That(flow.Check()).IsTrue().Because(string.Join('\n', flow.CheckLines()));
        string lines = string.Join('\n', flow.CheckLines());
        await Assert.That(lines).Contains("Signed:   Steam account (SteamID64 76561198000000001), ");
        await Assert.That(lines).Contains($"Settings: Steam account {FakeSteam.AccountId}'s (as the backup says)");
        await Assert.That(lines).Contains("signed them");
    }

    /// <summary>
    ///     A signature for another account than the one backed up (another person's sign-in raced this one) is saved
    ///     only on a yes: at the terminal acb asks, and the screens, which do not, write nothing and say why.
    /// </summary>
    [Test]
    public async Task BackupSign_ForAnotherAccount_AsksFirst_AndWritesNothingWithoutAYes()
    {
        using TestHost host = new() { IdentityService = Service, Interactive = true, IdentitySigner = new TestIdentity.Signer("76561198000000001") };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");
        host.Answers.Enqueue("n");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.All).Contains("someone else's sign-in reached acb first");
        await Assert.That(File.Exists(zip)).IsFalse();

        BackupFlow flow = new(host.Host()) { SteamPath = source.Root, OutputPath = zip, Sign = true };
        await Assert.That(flow.Look()).IsTrue();
        await Assert.That(flow.Save()).IsEqualTo(ExitCodes.Refused);
        await Assert.That(flow.SaveProblem).Contains("No backup was written");
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    /// <summary>
    ///     The backup screen, which writes no signed backup whose settings name no account, says so before anyone
    ///     signs in, so no sign-in or signature is wasted.
    /// </summary>
    [Test]
    public async Task TheBackupScreen_SaysASignedBackupNeedsTheAccountsParts_BeforeSigningIn()
    {
        TestIdentity.Signer signer = new();
        using TestHost host = new() { IdentityService = Service, Interactive = true, IdentitySigner = signer };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");
        BackupFlow flow = new(host.Host()) { SteamPath = source.Root, OutputPath = zip, Sign = true };
        await Assert.That(flow.Look()).IsTrue();
        flow.Choose(Sections.Cs2, false);
        flow.Choose(Sections.Launch, false);

        await Assert.That(flow.OutputProblem()).Contains("needs the CS2 settings or the launch options");
        await Assert.That(flow.Save()).IsEqualTo(ExitCodes.Refused);
        await Assert.That(signer.Signed.Count).IsEqualTo(0).Because("nothing was sent to be signed");
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    /// <summary>The restore screen names the signer it checked, not the manifest's own account, and warns when they differ.</summary>
    [Test]
    public async Task TheRestoreScreen_NamesTheSignerItChecked_AndWhoseSettingsTheyAre()
    {
        using TestHost host = new() { IdentityService = Service, Interactive = true, IdentitySigner = new TestIdentity.Signer("76561198000000001") };
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = host.Temp.Sub("signed.zip");
        host.Answers.Enqueue("y");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Done).Because(host.All);

        using RestoreScreen restore = new(new RestoreFlow(host.Host()) { ArchivePath = zip, SteamPath = target.Root });
        await Assert.That(restore.Shell.Next()).IsTrue().Because(restore.Shell.Notice);
        string summary = string.Join('\n', restore.Summary());

        await Assert.That(summary).Contains("signed by Steam account (SteamID64 76561198000000001), ");
        await Assert.That(summary).Contains($"Note: the settings are from {Identity.Account(FakeSteam.AccountId)}; account 39734273 (SteamID64 76561198000000001) signed them");
        await Assert.That(summary).Contains("settings of account " + FakeSteam.AccountId);
    }

    /// <summary>A policy naming Steam accounts accepts a backup signed by one of them, and refuses one signed by anyone else.</summary>
    [Test]
    public async Task APolicyNamingSteamAccounts_AcceptsOnlyTheirSignatures()
    {
        using TestHost host = new() { IdentityService = Service, Interactive = true };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Done).Because(host.All);
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
        await Assert.That(string.Join('\n', flow.DoneLines())).Contains($"Signed for Steam account (SteamID64 {TestIdentity.SteamId})");

        using TestHost none = new();
        BackupFlow unsigned = new(none.Host());
        await Assert.That(unsigned.SignProblem).Contains("does not know the CS2OpenDev service's address yet");
    }

    /// <summary>
    ///     Signing from the screens with no browser: the sign-in address still reaches the words the box shows, it
    ///     says the browser did not open, and Cancel ends the wait at once with nothing written.
    /// </summary>
    [Test]
    public async Task TheBackupScreen_WithNoBrowser_ShowsTheAddress_AndCancelStopsAtOnce()
    {
        StubService stub = new();
        using HttpClient http = new(stub);
        using TestHost host = new()
        {
            IdentityService = Service,
            Signer = (service, words, cancel) => new ServiceSigner(service, http, _ => false, words, cancel: cancel)
        };
        using FakeSteam steam = new();
        string zip = host.Temp.Sub("b.zip");
        BackupFlow flow = new(host.Host()) { SteamPath = steam.Root, OutputPath = zip, Sign = true };
        await Assert.That(flow.Look()).IsTrue();
        using CancellationTokenSource cancel = new();
        using CancelOnLine words = new("open the address above", cancel);

        DateTime started = DateTime.UtcNow;
        flow.Save(words, cancel.Token);

        await Assert.That(DateTime.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(30)).Because("Cancel does not wait out the sign-in limit");
        await Assert.That(words.ToString()).Contains("in a browser on this PC:\n  " + "https://login.cs2opendev.test/auth?");
        await Assert.That(words.ToString()).Contains("acb could not open a browser here");
        await Assert.That(flow.SaveProblem).Contains("Signing in was cancelled");
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    [Test]
    [Arguments(Service, null)]
    [Arguments("http://127.0.0.1:5000", "is not the CS2OpenDev service")]
    [Arguments("https://localhost:5001", "is not the CS2OpenDev service")]
    [Arguments("http://identity.cs2opendev.dev", "is not https")]
    [Arguments("https://user@identity.cs2opendev.dev", "not a service address")]
    [Arguments("identity.cs2opendev.dev", "not a service address")]
    [Arguments("https://identity.cs2opendev.dev", "is not the CS2OpenDev service")]
    public async Task OnlyTheKnownService_IsAServiceAddress_NotEvenAStandInOnThisPC(string service, string? expected)
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
        await Assert.That(words.ToString()).Contains("Signing in to the CS2OpenDev service at identity.cs2opendev.test with Steam");
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

    /// <summary>
    ///     Every address the service and its provider name is checked before the browser opens or a code is sent:
    ///     the known issuer only, a discovery document that names that issuer, and endpoints on the issuer's own
    ///     scheme, host and port.
    /// </summary>
    [Test]
    [Arguments(Service, """{ "issuer": "https://login.relay.example/realms/cs2opendev", "clientId": "acb", "scope": "openid" }""", null, "is not the sign-in service this acb knows")]
    [Arguments(Service, null, """{ "issuer": "x", "authorization_endpoint": "https://login.cs2opendev.test/auth", "token_endpoint": "https://login.cs2opendev.test/token" }""", "says it is 'x'")]
    [Arguments(Service, null, """{ "issuer": "https://login.cs2opendev.test/realms/cs2opendev", "authorization_endpoint": "https://login.cs2opendev.test/auth", "token_endpoint": "http://login.cs2opendev.test/token" }""", "token_endpoint")]
    [Arguments(Service, null, """{ "issuer": "https://login.cs2opendev.test/realms/cs2opendev", "authorization_endpoint": "ms-msdt:/id PCWDiagnostic", "token_endpoint": "https://login.cs2opendev.test/token" }""", "authorization_endpoint")]
    [Arguments(Service, null, """{ "issuer": "https://login.cs2opendev.test/realms/cs2opendev", "authorization_endpoint": "https://login.relay.example/auth", "token_endpoint": "https://login.cs2opendev.test/token" }""", "authorization_endpoint")]
    public async Task TheClient_RefusesASignInAddressItDoesNotTrust_BeforeOpeningOrSendingAnything(string service, string? config, string? discovery, string expected)
    {
        StubService stub = new() { ConfigJson = config ?? StubService.DefaultConfig, DiscoveryJson = discovery ?? StubService.DefaultDiscovery };
        using HttpClient http = new(stub);
        List<string> opened = [];
        ServiceSigner signer = new(service, http, url =>
        {
            opened.Add(url);
            return true;
        }, TextWriter.Null);

        await Assert.That(() => signer.Sign(new string('c', 64), Platforms.WindowsX64, new ArchiveTool("acb", "test")))
            .Throws<IdentityServiceException>().WithMessageContaining(expected);
        await Assert.That(opened).IsEmpty();
        await Assert.That(stub.TokenForm).IsEmpty();
        await Assert.That(stub.Bearer).IsNull();
    }

    /// <summary>
    ///     A stand-in on this PC is never signed through. Its sign-in page could send the browser on to the real
    ///     provider with acb's own redirect, state and challenge, so the real code would come back to acb, and acb
    ///     would hand it, with the verifier, to the stand-in's token endpoint to swap for a real token.
    /// </summary>
    [Test]
    public async Task AStandInOnThisPC_IsRefused_BeforeAnythingIsAskedOrOpened()
    {
        StubService stub = new();
        using HttpClient http = new(stub);
        List<string> opened = [];

        await Assert.That(() => new ServiceSigner("http://127.0.0.1:9000", http, url =>
            {
                opened.Add(url);
                return true;
            }, TextWriter.Null))
            .Throws<IdentityServiceException>().WithMessageContaining("is not the CS2OpenDev service");
        await Assert.That(opened).IsEmpty();
        await Assert.That(stub.ConfigCalls).IsEqualTo(0);
    }

    /// <summary>
    ///     Answers acb cannot use (a failure status, a body that is not JSON, a member missing) are refusals saying
    ///     what failed, never a crash.
    /// </summary>
    [Test]
    [Arguments("config-status", "the service answered 503")]
    [Arguments("config-missing", "the answer cannot be read")]
    [Arguments("config-forbidden", "ask the service where to sign in: the request was refused (the service answered 403")]
    [Arguments("token-busy", "finish signing in: the service is busy; try again later (the service answered 429")]
    [Arguments("discovery-not-json", "the answer cannot be read")]
    [Arguments("discovery-missing", "the answer cannot be read")]
    [Arguments("token-missing", "the answer cannot be read")]
    [Arguments("token-status", "the service answered 400")]
    [Arguments("sign-status", "the service answered 401")]
    [Arguments("sign-status", "run the backup again to sign in afresh")]
    [Arguments("sign-forbidden", "does not let this Steam account sign backups")]
    [Arguments("sign-busy", "try again in 2 minutes (the service answered 429")]
    [Arguments("sign-down", "cannot sign right now")]
    [Arguments("sign-null", "not a signature acb reads")]
    public async Task TheClient_RefusesAnAnswerItCannotUse(string fault, string expected)
    {
        StubService stub = new() { Fault = fault };
        using HttpClient http = new(stub);
        using HttpClient browserHttp = new();
        Task? visit = null;
        ServiceSigner signer = new(Service, http, url =>
        {
            Dictionary<string, string> query = Query(new Uri(url));
            stub.Challenge = query["code_challenge"];
            visit = Task.Run(async () => (await browserHttp.GetAsync(query["redirect_uri"] + "?code=the-code&state=" + Uri.EscapeDataString(query["state"]))).Dispose());
            return true;
        }, TextWriter.Null);

        await Assert.That(() => signer.Sign(new string('c', 64), Platforms.WindowsX64, new ArchiveTool("acb", "test")))
            .Throws<IdentityServiceException>().WithMessageContaining(expected);
        if (visit is not null)
        {
            await visit;
        }
    }

    [Test]
    public async Task TheClient_StopsWaiting_WhenNobodySignsIn()
    {
        StubService stub = new();
        using HttpClient http = new(stub);
        ServiceSigner signer = new(Service, http, _ => true, TextWriter.Null, TimeSpan.FromMilliseconds(300));

        await Assert.That(() => signer.Sign(new string('c', 64), Platforms.WindowsX64, new ArchiveTool("acb", "test")))
            .Throws<IdentityServiceException>().WithMessageContaining("so it has to be done in a browser on this PC");
    }

    /// <summary>
    ///     Another program on this PC that connects to the loopback port and sends a byte now and then holds nothing:
    ///     Cancel, and the sign-in limit, still end the sign-in at once, in the middle of its request.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task TheClient_StopsAConnectionThatTricklesBytes_OnCancelAndAtTheLimit(bool cancelIt)
    {
        StubService stub = new();
        using HttpClient http = new(stub);
        using CancellationTokenSource cancel = new();
        using CancellationTokenSource done = new();
        Task? trickle = null;
        ServiceSigner signer = new(Service, http, url =>
        {
            int port = new Uri(Query(new Uri(url))["redirect_uri"]).Port;
            trickle = Task.Run(async () =>
            {
                try
                {
                    using System.Net.Sockets.TcpClient client = new();
                    await client.ConnectAsync(IPAddress.Loopback, port, done.Token);
                    System.Net.Sockets.NetworkStream stream = client.GetStream();
                    for (int i = 0; i < 200 && !done.IsCancellationRequested; i++)
                    {
                        await stream.WriteAsync("G"u8.ToArray(), done.Token);
                        if (i == 2 && cancelIt)
                        {
                            await cancel.CancelAsync();
                        }

                        await Task.Delay(200, done.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
                {
                    // acb closed the connection, as it should.
                }
            });
            return true;
        }, TextWriter.Null, cancelIt ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(1), cancel.Token);

        DateTime started = DateTime.UtcNow;
        await Assert.That(() => signer.Sign(new string('c', 64), Platforms.WindowsX64, new ArchiveTool("acb", "test")))
            .Throws<IdentityServiceException>().WithMessageContaining(cancelIt ? "Signing in was cancelled" : "Nobody signed in");
        TimeSpan took = DateTime.UtcNow - started;
        await done.CancelAsync();
        await trickle!;

        await Assert.That(took).IsLessThan(TimeSpan.FromSeconds(5)).Because("the trickle neither outlasts Cancel nor the sign-in limit");
    }

    /// <summary>
    ///     <c>backup --sign</c> with the real client: a failed signing, or an answer that does not check, is a refusal
    ///     with nothing written, and nothing about undo records (a backup changes nothing on the PC).
    /// </summary>
    [Test]
    [Arguments("sign-status", "the service answered 401")]
    [Arguments("sign-forbidden", "does not let this Steam account sign backups")]
    [Arguments("sign-other-digest", "does not check")]
    [Arguments("sign-null", "not a signature acb reads")]
    public async Task BackupSign_WithAFailedSigning_IsRefused_AndWritesNothing(string fault, string expected)
    {
        StubService stub = new() { Fault = fault };
        using HttpClient http = new(stub);
        using HttpClient browserHttp = new();
        Task? visit = null;
        using TestHost host = new()
        {
            IdentityService = Service,
            Interactive = true,
            Signer = (service, words, cancel) => new ServiceSigner(service, http, url =>
            {
                Dictionary<string, string> query = Query(new Uri(url));
                stub.Challenge = query["code_challenge"];
                visit = Task.Run(async () => (await browserHttp.GetAsync(query["redirect_uri"] + "?code=the-code&state=" + Uri.EscapeDataString(query["state"]))).Dispose());
                return true;
            }, words, cancel: cancel)
        };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await visit!;

        await Assert.That(host.All).Contains(expected);
        await Assert.That(host.All).DoesNotContain("undo");
        await Assert.That(File.Exists(zip)).IsFalse();
        if (fault != "sign-other-digest")
        {
            await Assert.That(host.All).Contains(InfoCommands.NotSigned).Because("the service, not the disk, failed, and a backup can still be saved");
            await Assert.That(host.All).DoesNotContain("could not be written");
        }
    }

    /// <summary>
    ///     Ctrl+C while <c>backup --sign</c> waits for the sign-in stops it with nothing written, and the temp copies of
    ///     the settings it was made from are deleted. On Linux only their owner may open them while they are there.
    /// </summary>
    [Test]
    public async Task BackupSign_CtrlCWhileSigningIn_StopsIt_AndLeavesNoCopyOfTheSettings()
    {
        StubService stub = new();
        using HttpClient http = new(stub);
        string marker = Guid.NewGuid().ToString("N");
        string? staging = null;
        UnixFileMode? mode = null;
        TestHost? running = null;
        using TestHost host = new()
        {
            IdentityService = Service,
            Interactive = true,
            Signer = (service, words, cancel) => new ServiceSigner(service, http, _ =>
            {
                staging = Staging(marker);
                if (staging is not null && !OperatingSystem.IsWindows())
                {
                    mode = File.GetUnixFileMode(staging);
                }

                running!.Interrupt!();
                return true;
            }, words, cancel: cancel)
        };
        running = host;
        using FakeSteam source = new();
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo " + marker);
        string zip = host.Temp.Sub("signed.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Refused).Because(host.All);

        await Assert.That(host.All).Contains("Ctrl+C stops it");
        await Assert.That(host.All).Contains("Signing in was cancelled. " + InfoCommands.NotSigned);
        await Assert.That(staging).IsNotNull().Because("the copies are in temp while the sign-in waits");
        await Assert.That(Directory.Exists(staging)).IsFalse();
        await Assert.That(File.Exists(zip)).IsFalse();
        await Assert.That(host.Interrupt is null).IsTrue().Because("Ctrl+C ends acb as usual again once the run is over");
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Only the first Ctrl+C is caught; a second one ends acb, for a step that does not stop on the first.</summary>
    [Test]
    public async Task CtrlC_IsCaughtOnce_AndASecondEndsAcb()
    {
        int calls = 0;
        Func<bool> caught = AcbHost.FirstOnly(() => calls++);

        await Assert.That(caught()).IsTrue();
        await Assert.That(caught()).IsFalse();
        await Assert.That(caught()).IsFalse();
        await Assert.That(calls).IsEqualTo(1);
    }

    /// <summary>
    ///     Where a browser sign-in cannot come back to this PC (an SSH session, no desktop), <c>backup --sign</c> is
    ///     refused before anything is captured, and the backup screen's sign box says why it is off.
    /// </summary>
    [Test]
    public async Task BackupSign_WhereTheSignInCannotComeBack_IsRefusedBeforeAnythingIsCaptured()
    {
        string why = AcbHost.SignInProblem(name => name == "SSH_CONNECTION" ? "10.0.0.2 50000 10.0.0.1 22" : null, linux: false)!;
        using TestHost host = new() { IdentityService = Service, Interactive = true, BrowserSignInProblem = why };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("signed.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Refused).Because(host.All);

        await Assert.That(host.Output).Contains("SSH session");
        await Assert.That(host.Output).Contains("No backup was written");
        await Assert.That(host.Output).DoesNotContain("Saving:");
        await Assert.That(host.IdentitySigner.Signed).IsEmpty();
        await Assert.That(File.Exists(zip)).IsFalse();
        await Assert.That(new BackupFlow(host.Host()).SignProblem).Contains("SSH session");
    }

    [Test]
    [Arguments("SSH_CONNECTION", false, "SSH session")]
    [Arguments("SSH_TTY", true, "SSH session")]
    [Arguments("", true, "no desktop")]
    [Arguments("DISPLAY", true, null)]
    [Arguments("WAYLAND_DISPLAY", true, null)]
    [Arguments("", false, null)]
    public async Task TheSignInProblem_ComesFromTheSession(string set, bool linux, string? expected)
    {
        string? problem = AcbHost.SignInProblem(name => name == set ? "x" : null, linux);

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
    ///     A signed backup whose signature no longer checks (its delegation is not one this acb's roots signed, or its
    ///     key was revoked before it was signed) is refused by every command that reads it, and nothing is changed.
    /// </summary>
    [Test]
    [Arguments("other-roots", "signed by 0 of this version's root keys")]
    [Arguments("revoked", "revoked before it was signed")]
    public async Task ABackupWhoseSignatureDoesNotCheck_IsRefusedByEveryCommand_AndChangesNothing(string forgery, string expected)
    {
        using TestHost host = new() { IdentityService = Service, Interactive = true };
        using FakeSteam source = new();
        using FakeSteam target = new();
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo from the backup");
        string zip = host.Temp.Sub("signed.zip");
        host.Answers.Enqueue("y");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign")).IsEqualTo(ExitCodes.Done).Because(host.All);
        string digest = ArchiveReader.Open(zip, new ArchiveReadOptions { RequirePlatform = null }).ManifestSha256;
        using ECDsa first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DsseEnvelope delegation = forgery == "revoked"
            ? TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey, revokedFrom: DateTimeOffset.UtcNow.AddDays(-1))], TestIdentity.Roots[0], TestIdentity.Roots[1])
            : TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], first, second);
        string signature = Dsse.ToJson(new TestIdentity.Signer(delegation: delegation).Sign(digest, "", new ArchiveTool("", "")));
        using (System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update))
        {
            archive.GetEntry(ArchiveFormat.SignatureEntry)!.Delete();
            using StreamWriter writer = new(archive.CreateEntry(ArchiveFormat.SignatureEntry).Open(), new UTF8Encoding(false));
            await writer.WriteAsync(signature);
        }

        Dictionary<string, string> before = Contents(target.Root);
        // A revoked signature is still shown (RevokedAndPolicyTests); only using it needs a yes.
        string[][] commands = forgery == "revoked"
            ? [["verify", zip], ["plan", "--steam-path", target.Root, zip], ["restore", "--yes", "--steam-path", target.Root, zip]]
            : [["verify", zip], ["show", zip], ["plan", "--steam-path", target.Root, zip], ["restore", "--yes", "--steam-path", target.Root, zip]];

        foreach (string[] command in commands)
        {
            using TestHost run = new();
            await Assert.That(run.Run(command)).IsEqualTo(ExitCodes.Refused).Because(command[0] + ": " + run.All);
            await Assert.That(run.All).Contains(expected).Because(command[0]);
            await Assert.That(run.Records()).IsEmpty();
        }

        await Assert.That(Contents(target.Root)).IsEquivalentTo(before);
    }

    private static Dictionary<string, string> Contents(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllText);

    /// <summary>The temp folder a backup is being made in, found by a marker in its copy of autoexec.cfg; null when there is none.</summary>
    private static string? Staging(string marker)
    {
        foreach (string dir in Directory.EnumerateDirectories(Path.GetTempPath(), "acb-*"))
        {
            try
            {
                string copy = Path.Combine(dir, "pack", "files", "cs2game", "autoexec.cfg");
                if (File.Exists(copy) && File.ReadAllText(copy).Contains(marker, StringComparison.Ordinal))
                {
                    return dir;
                }
            }
            catch (IOException)
            {
                // Another test's folder, deleted while it was looked at.
            }
        }

        return null;
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    /// <summary>Words that cancel the sign-in once a line containing <paramref name="trigger" /> is written.</summary>
    private sealed class CancelOnLine(string trigger, CancellationTokenSource cancel) : StringWriter(System.Globalization.CultureInfo.InvariantCulture)
    {
        public override void WriteLine(string? value)
        {
            Write(value + "\n");
            if (value?.Contains(trigger, StringComparison.Ordinal) == true)
            {
                cancel.Cancel();
            }
        }
    }

    /// <summary>The service and its identity provider, answering as docs/identity-service.md says, signing with the test keys.</summary>
    private sealed class StubService : HttpMessageHandler
    {
        public const string DefaultConfig = $$"""{ "issuer": "{{Issuer}}", "clientId": "acb", "scope": "openid" }""";

        public const string DefaultDiscovery =
            $$"""{ "issuer": "{{Issuer}}", "authorization_endpoint": "https://login.cs2opendev.test/auth", "token_endpoint": "https://login.cs2opendev.test/token", "jwks_uri": "https://login.cs2opendev.test/certs" }""";

        public string ConfigJson { get; init; } = DefaultConfig;

        public string DiscoveryJson { get; init; } = DefaultDiscovery;

        /// <summary>Which answer goes wrong, and how; null for none.</summary>
        public string? Fault { get; init; }

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
                    return Fault switch
                    {
                        "config-status" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                        "config-forbidden" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                        "config-missing" => Json($$"""{ "issuer": "{{Issuer}}", "scope": "openid" }"""),
                        _ => Json(ConfigJson)
                    };
                case "/realms/cs2opendev/.well-known/openid-configuration":
                    return Fault switch
                    {
                        "discovery-not-json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>sign in</html>") },
                        "discovery-missing" => Json($$"""{ "issuer": "{{Issuer}}", "token_endpoint": "https://login.cs2opendev.test/token" }"""),
                        _ => Json(DiscoveryJson)
                    };
                case "/token":
                    TokenForm = body;
                    string verifier = Uri.UnescapeDataString(body.Split('&').Single(p => p.StartsWith("code_verifier=", StringComparison.Ordinal))["code_verifier=".Length..]);
                    VerifierMatched = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_') == Challenge;
                    return Fault switch
                    {
                        "token-status" => new HttpResponseMessage(HttpStatusCode.BadRequest),
                        "token-busy" => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
                        "token-missing" => Json("""{ "token_type": "Bearer", "expires_in": 300 }"""),
                        _ => Json("""{ "access_token": "token-123", "token_type": "Bearer", "expires_in": 300 }""")
                    };
                case "/v1/acb/sign":
                    Bearer = request.Headers.Authorization?.Parameter;
                    SignBody = body;
                    string digest = JsonDocument.Parse(body).RootElement.GetProperty("manifestSha256").GetString()!;
                    return Fault switch
                    {
                        "sign-status" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                        "sign-forbidden" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                        "sign-busy" => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                        {
                            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90)) }
                        },
                        "sign-down" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                        "sign-null" => Json("""{ "payloadType": "application/vnd.in-toto+json", "payload": null, "signatures": [] }"""),
                        "sign-other-digest" => Json(Dsse.ToJson(new TestIdentity.Signer().Sign(new string('0', 64), "", new ArchiveTool("", "")))),
                        _ => Json(Dsse.ToJson(new TestIdentity.Signer().Sign(digest, "", new ArchiveTool("", ""))))
                    };
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
