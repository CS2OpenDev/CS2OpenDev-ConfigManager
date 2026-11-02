using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     The Linux NVIDIA part: the driver's application profiles read as the driver reads them, and restored as one
///     file of acb's own, so the player's own profile files are never edited.
/// </summary>
public class LinuxNvidiaTests
{
    private const string PlayersRc = """
        # The player's own profiles. A "# in a string" is not a comment.
        {
          "rules": [
            { "pattern": { "feature": "procname", "matches": "glxgears" }, "profile": "other" },
            { "pattern": "cs2", "profile": "competitive" },
            { "pattern": { "op": "or", "sub": [ { "feature": "dso", "matches": "libfoo.so" } ] }, "profile": "other" },
            { "pattern": [ { "feature": "procname", "matches": "cs2" } ], "profile": [ "GLSyncToVblank", 1, "GLThreadedOptimizations", true ] }
          ],
          "profiles": [
            { "name": "other", "settings": [ "GLSyncToVblank", 1 ] }
          ]
        }
        """;

    // The profile the rc file names is defined later, in the .d folder, with the object form and a hex value.
    private const string PlayersRcD = """
        {
          "profiles": [
            { "name": "competitive", "settings": [
              { "k": "GLSyncToVblank", "v": 0 },
              { "key": "GLShaderDiskCachePath", "value": "/home/player/cache" },
              { "key": "GLSharpenValue", "value": 0x0F },
              { "key": "GLLogMaxAniso", "value": 010 }
            ] }
          ]
        }
        """;

    private static string Home(TempDir temp, string name, string? rc, string? rcd)
    {
        string home = temp.Sub(name);
        Directory.CreateDirectory(NvidiaProfiles.UserRcD(home));
        if (rc is not null)
        {
            File.WriteAllText(NvidiaProfiles.UserRc(home), rc);
        }

        if (rcd is not null)
        {
            File.WriteAllText(Path.Combine(NvidiaProfiles.UserRcD(home), "10-player"), rcd);
        }

        return home;
    }

    [Test]
    public async Task Normalize_DropsComments_AndWritesHexAndOctalInDecimal()
    {
        string plain = NvidiaProfiles.Normalize("""{ "a": 0x1F, "b": 017, "c": "x # y", "d": -0x2 } # gone""");

        await Assert.That(plain.Trim()).IsEqualTo("""{ "a": 31, "b": 15, "c": "x # y", "d": -2 }""");
    }

    /// <summary>
    ///     cs2 gets the first value of each key from the rules that plainly match it, a profile named before it is
    ///     defined included; only carried keys travel, and what was left is said.
    /// </summary>
    [Test]
    public async Task Cs2Settings_ResolveAsTheDriverDoes_AndCarryOnlyTheListedKeys()
    {
        using TempDir temp = new();
        string home = Home(temp, "player", PlayersRc, PlayersRcD);
        List<string> notes = [];

        List<NvProfileSetting> settings = NvidiaProfiles.Cs2Settings(home, notes);

        await Assert.That(settings).IsEquivalentTo([
            new NvProfileSetting("GLSyncToVblank", "number", "0"),
            new NvProfileSetting("GLSharpenValue", "number", "15"),
            new NvProfileSetting("GLLogMaxAniso", "number", "8"),
            new NvProfileSetting("GLThreadedOptimizations", "bool", "true")
        ]);
        await Assert.That(notes).Contains("GLShaderDiskCachePath is not carried");
        await Assert.That(notes).Contains("1 rule with a compound or library pattern were not read as cs2's");
        await Assert.That(NvidiaProfiles.RcHasCs2Rule(home)).IsTrue();
    }

    [Test]
    public async Task ASettingOutsideTheList_OrOfTheWrongKind_IsRefused()
    {
        await Assert.That(NvidiaProfiles.SettingProblem("GLShaderDiskCachePath", "string", "/tmp")).IsNotNull();
        await Assert.That(NvidiaProfiles.SettingProblem("GLSyncToVblank", "bool", "1")).IsNotNull();
        await Assert.That(NvidiaProfiles.SettingProblem("GLSyncToVblank", "number", "NaN")).IsNotNull();
        await Assert.That(NvidiaProfiles.SettingProblem("GLSharpenValue", "string", "a\nb")).IsNotNull();
        await Assert.That(NvidiaProfiles.SettingProblem("GLThreadedOptimizations", "bool", "false")).IsNull();
    }

    /// <summary>
    ///     Captured on one PC, restored onto another as acb's own file in the .d folder: the target's own files are
    ///     left byte for byte, its rc rule for cs2 is named as winning, and undo removes acb's file again.
    /// </summary>
    [Test]
    public async Task RestoreAndUndo_WriteOnlyAcbsOwnFile()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("the NVIDIA application profiles are the Linux driver's");
            return;
        }

        using TempDir temp = new();
        using FakeSteam steam = new();
        string playerHome = Home(temp, "player", PlayersRc, PlayersRcD);
        string targetRc = """{ "rules": [ { "pattern": "cs2", "profile": [ "GLYield", "NOTHING" ] } ] }""";
        string targetHome = Home(temp, "target", targetRc, """{ "rules": [] }""");
        string capture = temp.Sub("capture");
        CaptureOptions capturing = new() { SteamRoot = steam.Root, Creator = "test", Machine = new FakeMachine { Home = playerHome } };
        capturing.Include.Clear();
        capturing.Include.Add(Sections.Nvidia);
        SectionResult captured = ConfigBackup.Capture(capturing, capture, TextWriter.Null).Results.Single();
        await Assert.That(captured.Outcome).IsEqualTo(Outcomes.Done).Because(captured.ToString());

        FakeMachine target = new() { Home = targetHome };
        string undoRoot = temp.Sub("undo");
        RunReport restore = Engine.Restore(capture, new RestoreOptions
        {
            Only = [Sections.Nvidia], SteamRoot = steam.Root, SkipProcessChecks = true, UndoRoot = undoRoot, Machine = target
        });

        SectionResult restored = restore.Results.Single();
        await Assert.That(restored.Outcome).IsEqualTo(Outcomes.Done).Because(restored.ToString());
        await Assert.That(restored.Detail).Contains("its values win where both set one");
        string own = await File.ReadAllTextAsync(NvidiaProfiles.OwnFile(targetHome));
        List<string> notes = [];
        await Assert.That(NvidiaProfiles.Cs2Settings(targetHome, notes).Exists(s => s is { Key: "GLThreadedOptimizations", Value: "true" })).IsTrue().Because(own);
        await Assert.That(await File.ReadAllTextAsync(NvidiaProfiles.UserRc(targetHome))).IsEqualTo(targetRc);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(NvidiaProfiles.UserRcD(targetHome), "10-player"))).IsEqualTo("""{ "rules": [] }""");

        UndoRecord record = new UndoStore(undoRoot, isMachine: false).Records().Single();
        RunReport undo = Engine.Undo(record.Dir, new RestoreOptions { SteamRoot = steam.Root, SkipProcessChecks = true, Machine = target, WriteUndo = false });

        await Assert.That(undo.Results.Single().Outcome).IsEqualTo(Outcomes.Done).Because(undo.Results.Single().ToString());
        await Assert.That(File.Exists(NvidiaProfiles.OwnFile(targetHome))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(NvidiaProfiles.UserRc(targetHome))).IsEqualTo(targetRc);
    }

    /// <summary>A .d "folder" that is a file blocks the part before anything is written; the player's file is not touched.</summary>
    [Test]
    public async Task ADotDThatIsAFile_BlocksThePart()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("the NVIDIA application profiles are the Linux driver's");
            return;
        }

        using TempDir temp = new();
        using FakeSteam steam = new();
        string playerHome = Home(temp, "player", PlayersRc, PlayersRcD);
        string targetHome = temp.Sub("target");
        Directory.CreateDirectory(NvidiaProfiles.NvFolder(targetHome));
        File.WriteAllText(NvidiaProfiles.UserRcD(targetHome), "not a folder");
        string capture = temp.Sub("capture");
        CaptureOptions capturing = new() { SteamRoot = steam.Root, Creator = "test", Machine = new FakeMachine { Home = playerHome } };
        capturing.Include.Clear();
        capturing.Include.Add(Sections.Nvidia);
        ConfigBackup.Capture(capturing, capture, TextWriter.Null);

        SectionResult result = Engine.Restore(capture, new RestoreOptions
        {
            Only = [Sections.Nvidia], SteamRoot = steam.Root, SkipProcessChecks = true, UndoRoot = temp.Sub("undo"), Machine = new FakeMachine { Home = targetHome }
        }).Results.Single();

        await Assert.That(result.Outcome).IsEqualTo(Outcomes.Blocked).Because(result.ToString());
        await Assert.That(await File.ReadAllTextAsync(NvidiaProfiles.UserRcD(targetHome))).IsEqualTo("not a folder");
    }
}
