using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Tests;

public class KeyValuesTests
{
    [Test]
    public async Task ParsesTheShapeCs2WritesItsSettingsIn()
    {
        KvNode root = KvNode.Parse("\"config\"\n{\n\t\"bindings\"\n\t{\n\t\t\"MOUSE4\"\t\t\"slot7\"\n\t\t\"PGDN\"\t\t\"say \\\"gg\\\"\"\n\t}\n}\n");

        await Assert.That(root.Key).IsEqualTo("config");
        await Assert.That(root.Path("bindings")!.Get("MOUSE4")).IsEqualTo("slot7");
        await Assert.That(root.Path("bindings")!.Get("PGDN")).IsEqualTo("say \"gg\"");
    }

    [Test]
    public async Task SkipsCommentsAndConditionals_AndReadsUnquotedTokens()
    {
        KvNode root = KvNode.Parse("// header\n\"a\"\n{\n\tkey value // trailing\n\t\"b\" \"c\" [$WIN32]\n\t\"d\" { }\n}");

        await Assert.That(root.Get("key")).IsEqualTo("value");
        await Assert.That(root.Get("b")).IsEqualTo("c");
        await Assert.That(root.Child("d")!.IsSection).IsTrue();
    }

    [Test]
    public async Task RoundTripsKeepingOrderAndRepeatedKeys()
    {
        string text = "\"r\"\n{\n\t\"x\"\t\t\"1\"\n\t\"x\"\t\t\"2\"\n\t\"s\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Steam\"\n\t}\n}\n";

        KvNode root = KvNode.Parse(text);

        await Assert.That(root.ToText()).IsEqualTo(text);
        await Assert.That(root.Path("s")!.Get("path")).IsEqualTo("C:\\Steam");
    }

    [Test]
    public async Task SetReplacesInPlace_AndSectionCreatesAtTheEnd()
    {
        KvNode root = KvNode.Parse("\"r\" { \"a\" \"1\" \"b\" \"2\" }");

        root.Set("a", "9");
        root.Section("c").Set("d", "e");

        await Assert.That(root.ToText()).IsEqualTo("\"r\"\n{\n\t\"a\"\t\t\"9\"\n\t\"b\"\t\t\"2\"\n\t\"c\"\n\t{\n\t\t\"d\"\t\t\"e\"\n\t}\n}\n");
    }

    [Test]
    [Arguments("\"r\" {")]
    [Arguments("\"r\" { \"a\" }")]
    [Arguments("\"r\" { \"a\" \"unterminated }")]
    [Arguments("}")]
    [Arguments("")]
    public async Task RefusesBrokenText(string text)
    {
        await Assert.That(() => KvNode.Parse(text)).Throws<FormatException>();
    }
}
