using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CS2OpenDev.ConfigManager.Kit;

/// <summary>One setting of a Linux NVIDIA application profile: its key, "bool", "number" or "string", and its value as text.</summary>
internal sealed record NvProfileSetting(string Key, string Kind, string Value);

/// <summary>
///     The Linux NVIDIA driver's application profiles (README appendix "Application Profiles"): JSON with "#"
///     comments and hex or octal numbers, in ~/.nv/nvidia-application-profiles-rc (a file or a folder of files) and
///     then ~/.nv/nvidia-application-profiles-rc.d. Capture reads the settings rules that plainly match the cs2
///     process give it; restore never edits the player's files, it writes one file of its own into the .d folder.
/// </summary>
internal static class NvidiaProfiles
{
    /// <summary>The file acb writes into the .d folder, and acb undo removes.</summary>
    public const string OwnFileName = "50-acb-cs2";

    /// <summary>The process name CS2 runs as on Linux.</summary>
    public const string Cs2Process = "cs2";

    /// <summary>
    ///     The keys that travel: what changes how CS2 renders or paces frames. Left out on purpose: paths
    ///     (GLShaderDiskCachePath), device choices that name this PC's hardware (GLSyncDisplayDevice, EGLVisible*),
    ///     and the keys that loosen protocol or security checks.
    /// </summary>
    public static readonly string[] CarriedKeys =
    [
        "GLFSAAMode", "GLLogMaxAniso", "GLSingleThreaded", "GLSyncToVblank", "GLShaderDiskCache", "GLYield",
        "GLThreadedOptimizations", "GLAllowFXAAUsage", "GLVRRAllowed", "GLShowGraphicsOSD", "GLSharpenEnable",
        "GLSharpenValue", "GLSharpenIgnoreFilmGrain"
    ];

    private const int MaxValueLength = 256;

    public static string NvFolder(string home) => Path.Combine(home, ".nv");

    public static string UserRc(string home) => Path.Combine(NvFolder(home), "nvidia-application-profiles-rc");

    public static string UserRcD(string home) => Path.Combine(NvFolder(home), "nvidia-application-profiles-rc.d");

    public static string OwnFile(string home) => Path.Combine(UserRcD(home), OwnFileName);

    /// <summary>The player's profile files in the order the driver reads them: the rc file (or its folder's files), then the .d folder's.</summary>
    public static List<string> Files(string home) => FilesIn([UserRc(home), UserRcD(home)]);

    private static List<string> FilesIn(string[] places)
    {
        List<string> files = [];
        foreach (string place in places)
        {
            if (File.Exists(place))
            {
                files.Add(place);
            }
            else if (Directory.Exists(place))
            {
                files.AddRange(Directory.GetFiles(place).Order(StringComparer.Ordinal));
            }
        }

        return files;
    }

    /// <summary>
    ///     The carried settings the cs2 process gets from the player's files, the first rule to set a key winning as
    ///     in the driver. <paramref name="notes" /> says what was left: other keys, rules whose pattern cannot be read
    ///     as plainly cs2's, and files that cannot be read.
    /// </summary>
    public static List<NvProfileSetting> Cs2Settings(string home, List<string> notes) => Cs2Settings(Files(home), notes);

    private static List<NvProfileSetting> Cs2Settings(List<string> files, List<string> notes)
    {
        List<JsonElement> roots = [];
        List<JsonDocument> documents = [];
        try
        {
            foreach (string file in files)
            {
                try
                {
                    JsonDocument document = JsonDocument.Parse(Normalize(File.ReadAllText(file)), new JsonDocumentOptions { AllowTrailingCommas = true });
                    documents.Add(document);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        roots.Add(document.RootElement);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    notes.Add(Path.GetFileName(file) + " cannot be read (" + ex.Message + ")");
                }
            }

            // Profiles may be named before they are defined, in any file; the first definition of a name is used.
            Dictionary<string, JsonElement> profiles = new(StringComparer.Ordinal);
            foreach (JsonElement root in roots)
            {
                if (root.TryGetProperty("profiles", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement profile in list.EnumerateArray())
                    {
                        if (profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("name", out JsonElement name)
                                                                       && name.ValueKind == JsonValueKind.String && profile.TryGetProperty("settings", out JsonElement settings))
                        {
                            profiles.TryAdd(name.GetString()!, settings);
                        }
                    }
                }
            }

            List<NvProfileSetting> found = [];
            HashSet<string> set = new(StringComparer.Ordinal);
            int unclear = 0;
            foreach (JsonElement root in roots)
            {
                if (!root.TryGetProperty("rules", out JsonElement rules) || rules.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement rule in rules.EnumerateArray())
                {
                    if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("pattern", out JsonElement pattern) || !rule.TryGetProperty("profile", out JsonElement profile))
                    {
                        continue;
                    }

                    bool? matches = MatchesCs2(pattern);
                    if (matches is null)
                    {
                        unclear++;
                        continue;
                    }

                    if (matches == false)
                    {
                        continue;
                    }

                    JsonElement? settings = profile.ValueKind switch
                    {
                        JsonValueKind.String => profiles.TryGetValue(profile.GetString()!, out JsonElement named) ? named : null,
                        JsonValueKind.Array => profile,
                        JsonValueKind.Object when profile.TryGetProperty("settings", out JsonElement inline) => inline,
                        _ => null
                    };
                    foreach ((string key, JsonElement value) in Settings(settings))
                    {
                        if (!set.Add(key))
                        {
                            continue;
                        }

                        if (Array.IndexOf(CarriedKeys, key) < 0)
                        {
                            notes.Add(key + " is not carried");
                            continue;
                        }

                        if (Setting(key, value) is { } carried)
                        {
                            found.Add(carried);
                        }
                    }
                }
            }

            if (unclear > 0)
            {
                notes.Add(unclear + (unclear == 1 ? " rule" : " rules") + " with a compound or library pattern were not read as cs2's");
            }

            return found;
        }
        finally
        {
            foreach (JsonDocument document in documents)
            {
                document.Dispose();
            }
        }
    }

    /// <summary>
    ///     Why a backup's or record's setting cannot be written, or null: a key outside the carried list, a kind
    ///     other than bool, number or string, or a value that is not one of that kind.
    /// </summary>
    public static string? SettingProblem(string key, string kind, string value)
    {
        if (Array.IndexOf(CarriedKeys, key) < 0)
        {
            return "the backup's NVIDIA profile names " + key + ", which is not carried";
        }

        bool fits = kind switch
        {
            "bool" => value is "true" or "false",
            "number" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n),
            "string" => value.Length <= MaxValueLength && !value.Any(char.IsControl),
            _ => false
        };
        return fits ? null : "the backup's NVIDIA profile gives " + key + " a value that is not a " + kind;
    }

    /// <summary>The file acb writes: one rule for the cs2 process and one profile with the settings, as plain JSON.</summary>
    public static byte[] Write(IReadOnlyList<NvProfileSetting> settings)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartArray("rules");
            json.WriteStartObject();
            json.WriteStartObject("pattern");
            json.WriteString("feature", "procname");
            json.WriteString("matches", Cs2Process);
            json.WriteEndObject();
            json.WriteString("profile", "acb-cs2");
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteStartArray("profiles");
            json.WriteStartObject();
            json.WriteString("name", "acb-cs2");
            json.WriteStartArray("settings");
            foreach (NvProfileSetting setting in settings)
            {
                json.WriteStartObject();
                json.WriteString("key", setting.Key);
                json.WritePropertyName("value");
                switch (setting.Kind)
                {
                    case "bool":
                        json.WriteBooleanValue(setting.Value == "true");
                        break;
                    case "number":
                        json.WriteNumberValue(double.Parse(setting.Value, NumberStyles.Float, CultureInfo.InvariantCulture));
                        break;
                    default:
                        json.WriteStringValue(setting.Value);
                        break;
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }

        string header = "# Written by acb (CS2OpenDev ConfigManager) for CS2's restored NVIDIA settings; acb undo removes it.\n";
        return [.. Encoding.UTF8.GetBytes(header), .. stream.ToArray(), (byte)'\n'];
    }

    /// <summary>
    ///     Whether the player's own rc file (not the .d folder, where acb writes) gives cs2 any setting. The driver
    ///     reads that file first, so its settings win over acb's.
    /// </summary>
    public static bool RcHasCs2Rule(string home)
    {
        List<string> notes = [];
        return Cs2Settings(FilesIn([UserRc(home)]), notes).Count > 0 || notes.Exists(n => n.EndsWith(" is not carried", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The driver's JSON superset as plain JSON: "#" comments outside strings removed, and hex (0x1F) and octal
    ///     (017) numbers written in decimal.
    /// </summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder plain = new(text.Length);
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                plain.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    plain.Append(text[++i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                plain.Append(c);
            }
            else if (c == '#')
            {
                while (i + 1 < text.Length && text[i + 1] != '\n')
                {
                    i++;
                }
            }
            else if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                int start = i;
                while (i + 1 < text.Length && (char.IsAsciiLetterOrDigit(text[i + 1]) || text[i + 1] is '.' or '+' or '-'))
                {
                    i++;
                }

                plain.Append(Number(text[start..(i + 1)]));
            }
            else
            {
                plain.Append(c);
            }
        }

        return plain.ToString();
    }

    private static string Number(string token)
    {
        bool negative = token.StartsWith('-');
        string digits = negative ? token[1..] : token;
        if (digits.Length > 2 && digits[0] == '0' && digits[1] is 'x' or 'X'
                              && long.TryParse(digits[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long hex))
        {
            return (negative ? -hex : hex).ToString(CultureInfo.InvariantCulture);
        }

        if (digits.Length > 1 && digits[0] == '0' && digits.All(char.IsAsciiDigit))
        {
            try
            {
                long octal = Convert.ToInt64(digits, 8);
                return (negative ? -octal : octal).ToString(CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                // Not octal after all (an 8 or 9); left for the JSON parser to refuse.
            }
        }

        return token;
    }

    /// <summary>True when a pattern plainly names the cs2 process, false when it plainly names another, null when it cannot be told.</summary>
    private static bool? MatchesCs2(JsonElement pattern)
    {
        switch (pattern.ValueKind)
        {
            case JsonValueKind.String:
                return pattern.GetString() == Cs2Process;
            case JsonValueKind.Array:
                // Every pattern must match; an empty list matches every process, cs2 included.
                bool all = true;
                foreach (JsonElement part in pattern.EnumerateArray())
                {
                    bool? one = MatchesCs2(part);
                    if (one is null)
                    {
                        return null;
                    }

                    all &= one.Value;
                }

                return all;
            case JsonValueKind.Object when pattern.TryGetProperty("feature", out JsonElement feature) && feature.ValueKind == JsonValueKind.String:
                string? name = feature.GetString();
                if (name == "true")
                {
                    return true;
                }

                if (name is "procname" or "commname" && pattern.TryGetProperty("matches", out JsonElement matches) && matches.ValueKind == JsonValueKind.String)
                {
                    return matches.GetString() == Cs2Process;
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>A profile's settings in either documented form: a flat key, value list, or objects with k/key and v/value.</summary>
    private static IEnumerable<(string Key, JsonElement Value)> Settings(JsonElement? settings)
    {
        if (settings is not { ValueKind: JsonValueKind.Array } list)
        {
            yield break;
        }

        JsonElement[] items = [.. list.EnumerateArray()];
        if (items.Length > 0 && items[0].ValueKind == JsonValueKind.String)
        {
            for (int i = 0; i + 1 < items.Length; i += 2)
            {
                if (items[i].ValueKind == JsonValueKind.String)
                {
                    yield return (items[i].GetString()!, items[i + 1]);
                }
            }

            yield break;
        }

        foreach (JsonElement item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            JsonElement key = item.TryGetProperty("key", out JsonElement k1) ? k1 : item.TryGetProperty("k", out JsonElement k2) ? k2 : default;
            JsonElement value = item.TryGetProperty("value", out JsonElement v1) ? v1 : item.TryGetProperty("v", out JsonElement v2) ? v2 : default;
            if (key.ValueKind == JsonValueKind.String && value.ValueKind != JsonValueKind.Undefined)
            {
                yield return (key.GetString()!, value);
            }
        }
    }

    private static NvProfileSetting? Setting(string key, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => new NvProfileSetting(key, "bool", "true"),
        JsonValueKind.False => new NvProfileSetting(key, "bool", "false"),
        JsonValueKind.Number => new NvProfileSetting(key, "number", value.GetRawText()),
        JsonValueKind.String when SettingProblem(key, "string", value.GetString()!) is null => new NvProfileSetting(key, "string", value.GetString()!),
        _ => null
    };
}
