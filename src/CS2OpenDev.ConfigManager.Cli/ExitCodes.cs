using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>acb's exit codes (docs/design.md, "Commands"). A higher code wins when a run has several outcomes.</summary>
internal static class ExitCodes
{
    /// <summary>Done, nothing left.</summary>
    public const int Done = 0;

    /// <summary>Refused before any change: verification, platform, format, policy, an undo record that could not be claimed, or no consent.</summary>
    public const int Refused = 1;

    /// <summary>Done with parts left for later: CS2 or Steam running, an administrator needed, a target that cannot be written, a waiting undo.</summary>
    public const int PartsLeft = 2;

    /// <summary>A part failed; a restore put back what it had changed.</summary>
    public const int PartFailed = 3;

    /// <summary>Undo records need attention: a rollback that failed, a record that cannot be read.</summary>
    public const int RecordsNeedAttention = 4;

    public const int Usage = 64;

    /// <summary>The code for one run's results: failed beats left, and done or skipped leaves nothing.</summary>
    public static int For(IEnumerable<SectionResult> results)
    {
        int code = Done;
        foreach (SectionResult result in results)
        {
            code = Math.Max(code, result.Outcome switch
            {
                "failed" => PartFailed,
                "deferred" or "admin" or "blocked" or "left" => PartsLeft,
                _ => Done
            });
        }

        return code;
    }

    /// <summary>The code for an undo run over one store.</summary>
    public static int For(IReadOnlyList<RecordOutcome> outcomes)
    {
        int code = Done;
        foreach (RecordOutcome outcome in outcomes)
        {
            if (outcome.Unreadable is not null)
            {
                code = Math.Max(code, RecordsNeedAttention);
                continue;
            }

            if (outcome.Report is { } report)
            {
                code = Math.Max(code, For(report.Results));
            }

            if (outcome.Left.Count > 0)
            {
                code = Math.Max(code, PartsLeft);
            }
        }

        return code;
    }
}
