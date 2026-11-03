using System.Text.Json;
using System.Text.Json.Serialization;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>One part's outcome. <paramref name="LeftByAdministrator" /> marks a part an administrator's acb leaves, as it is written through the Steam folder.</summary>
internal sealed record SectionLine(string Section, string Label, string Outcome, string Detail, bool? LeftByAdministrator = null);

/// <summary>One undo record in a report; Problem says why an unreadable one cannot be read.</summary>
internal sealed record RecordLine(string Name, string? Created, string? Archive, IReadOnlyList<string> Parts, string State, string? Problem = null);

/// <summary>
///     What a command did, as --json prints it and --report saves it. A plan's report is also the plan file
///     <c>acb restore --plan</c> applies: it names the archive by path and digest, the options, and a fingerprint of
///     what the plan saw on the PC.
/// </summary>
internal sealed class CommandReport
{
    public const string CurrentFormat = "acb-report/1";

    public string Format { get; set; } = CurrentFormat;

    /// <summary>The acb that made the report. A plan applies only with the same version, whose parts say the same things.</summary>
    public string AcbVersion { get; set; } = Archives.Version();

    public string Command { get; set; } = "";

    public int ExitCode { get; set; }

    public string? Archive { get; set; }

    public string? ArchiveSha256 { get; set; }

    public string? Level { get; set; }

    public string? Platform { get; set; }

    public DateTimeOffset? Created { get; set; }

    public string? SteamAccount { get; set; }

    /// <summary>The SHA-256 of the restorer a self-contained backup carries; null when it carries none.</summary>
    public string? RestorerSha256 { get; set; }

    /// <summary>Whether that restorer is byte for byte the acb that made the report.</summary>
    public bool? RestorerIsThisAcb { get; set; }

    public List<string>? Only { get; set; }

    public string? Account { get; set; }

    public string? SteamPath { get; set; }

    public string? Policy { get; set; }

    public string? PolicySha256 { get; set; }

    /// <summary>The plan also writes the backup's game state integration files (--gsi); null when it does not.</summary>
    public bool? GameStateIntegration { get; set; }

    public string? Fingerprint { get; set; }

    public List<SectionLine> Sections { get; set; } = [];

    public string? UndoRecord { get; set; }

    public List<RecordLine>? Records { get; set; }

    public List<string> Notes { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CommandReport))]
internal sealed partial class CliJson : JsonSerializerContext;

/// <summary>
///     Where a command's words go. With --json, standard output carries only the JSON report and the words go to
///     standard error, so a script can read one and a person the other.
/// </summary>
internal sealed class Output
{
    private readonly AcbHost _host;
    private readonly bool _json;
    private readonly string? _reportPath;

    public Output(AcbHost host, string command, bool json, string? reportPath)
    {
        _host = host;
        _json = json;
        _reportPath = reportPath;
        Report = new CommandReport { Command = command };
    }

    public CommandReport Report { get; }

    public TextWriter Text => _json ? _host.Err : _host.Out;

    public void Line(string text = "") => Text.WriteLine(Printable(text));

    public void Note(string text)
    {
        Report.Notes.Add(text);
        Text.WriteLine(Printable(text));
    }

    public void Result(SectionResult result) => Result(result.Section, result.Outcome, result.Detail);

    /// <summary>Reports one part; an outcome that is not one of <see cref="Outcomes.All" /> throws.</summary>
    public void Result(string section, string outcome, string detail, bool leftByAdministrator = false)
    {
        if (!Outcomes.IsKnown(outcome))
        {
            throw new ArgumentException("Not an outcome: " + outcome, nameof(outcome));
        }

        Report.Sections.Add(new SectionLine(section, Sections.Label(section), outcome, detail, leftByAdministrator ? true : null));
        Text.WriteLine(Printable($"  {outcome,-9} {Sections.Label(section)}: {detail}"));
    }

    /// <summary>
    ///     Text as a terminal shows it as written: control characters, line and paragraph separators and the marks
    ///     that reorder text are written as escapes such as \r and \x1b, so a backup's names, notes and values cannot
    ///     move the cursor, hide a line of the plan or add one. <paramref name="keepLines" /> keeps line breaks and
    ///     tabs, for printing a file. The JSON report keeps the text as it is, escaped by JSON.
    /// </summary>
    public static string Printable(string text, bool keepLines = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Any(c => NeedsEscape(c, keepLines)))
        {
            return text;
        }

        System.Text.StringBuilder shown = new(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (keepLines && c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                shown.Append(c);
            }
            else if (!NeedsEscape(c, keepLines))
            {
                shown.Append(c);
            }
            else
            {
                shown.Append(c switch
                {
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    < '\u0100' => "\\x" + ((int)c).ToString("x2", System.Globalization.CultureInfo.InvariantCulture),
                    _ => "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)
                });
            }
        }

        return shown.ToString();
    }

    private static bool NeedsEscape(char c, bool keepLines) =>
        !(keepLines && c is '\n' or '\t')
        && (char.IsControl(c) || c is '\u2028' or '\u2029' or (>= '\u202a' and <= '\u202e') or (>= '\u2066' and <= '\u2069') or '\u200e' or '\u200f');

    public int Refuse(string why)
    {
        Note("Refused: " + why);
        return Finish(ExitCodes.Refused);
    }

    public int Finish(int code)
    {
        Report.ExitCode = code;
        if (_json)
        {
            _host.Out.WriteLine(JsonSerializer.Serialize(Report, CliJson.Default.CommandReport));
        }

        if (_reportPath is not null)
        {
            try
            {
                File.WriteAllText(_reportPath, JsonSerializer.Serialize(Report, CliJson.Default.CommandReport));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _host.Err.WriteLine($"The report could not be saved to {_reportPath}: {ex.Message}");
            }
        }

        return code;
    }
}
