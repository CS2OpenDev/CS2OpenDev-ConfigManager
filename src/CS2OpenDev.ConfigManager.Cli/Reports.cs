using System.Text.Json;
using System.Text.Json.Serialization;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Cli;

internal sealed record SectionLine(string Section, string Label, string Outcome, string Detail);

internal sealed record RecordLine(string Name, string? Created, string? Archive, IReadOnlyList<string> Parts, string State);

/// <summary>
///     What a command did, as --json prints it and --report saves it. A plan's report is also the plan file
///     <c>acb restore --plan</c> applies: it names the archive by path and digest, the options, and a fingerprint of
///     what the plan saw on the PC.
/// </summary>
internal sealed class CommandReport
{
    public string Format { get; set; } = "acb-report/1";

    public string Command { get; set; } = "";

    public int ExitCode { get; set; }

    public string? Archive { get; set; }

    public string? ArchiveSha256 { get; set; }

    public string? Level { get; set; }

    public string? Platform { get; set; }

    public DateTimeOffset? Created { get; set; }

    public string? SteamAccount { get; set; }

    public List<string>? Only { get; set; }

    public string? Account { get; set; }

    public string? SteamPath { get; set; }

    public string? Policy { get; set; }

    public string? PolicySha256 { get; set; }

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

    public void Line(string text = "") => Text.WriteLine(text);

    public void Note(string text)
    {
        Report.Notes.Add(text);
        Text.WriteLine(text);
    }

    public void Result(SectionResult result) => Result(result.Section, result.Outcome, result.Detail);

    public void Result(string section, string outcome, string detail)
    {
        Report.Sections.Add(new SectionLine(section, Sections.Label(section), outcome, detail));
        Text.WriteLine($"  {outcome,-9} {Sections.Label(section)}: {detail}");
    }

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
