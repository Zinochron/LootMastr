using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace LootMastr.Data;

public readonly record struct JobInfo(uint Id, string Abbreviation, string Name, RaidRole Role, uint IconId)
{
    public bool IsValid => Id != 0;
}

/// <summary>Job names, abbreviations and roles from the <c>ClassJob</c> sheet.</summary>
public sealed class JobCatalog
{
    /// <summary>Job icons with the role coloured frame, one per <c>ClassJob</c> row.</summary>
    private const uint JobIconBase = 62100;

    private readonly Lazy<Dictionary<uint, JobInfo>> jobs;

    public JobCatalog() => jobs = new Lazy<Dictionary<uint, JobInfo>>(Build, isThreadSafe: true);

    public IReadOnlyDictionary<uint, JobInfo> All => jobs.Value;

    public JobInfo Get(uint jobId) =>
        jobs.Value.TryGetValue(jobId, out var info)
            ? info
            : new JobInfo(0, "???", "Unknown", RaidRole.Unknown, 0);

    public RaidRole RoleOf(uint jobId) => Get(jobId).Role;

    /// <summary>Looks a job up by its three letter abbreviation, as gear planners spell it.</summary>
    public JobInfo FindByAbbreviation(string abbreviation)
    {
        if (string.IsNullOrWhiteSpace(abbreviation))
            return default;

        foreach (var job in jobs.Value.Values)
        {
            if (string.Equals(job.Abbreviation, abbreviation.Trim(), StringComparison.OrdinalIgnoreCase))
                return job;
        }

        return default;
    }

    private static Dictionary<uint, JobInfo> Build()
    {
        var sheet = Services.Data.GetExcelSheet<ClassJob>();
        var result = new Dictionary<uint, JobInfo>();

        foreach (var row in sheet)
        {
            if (row.RowId == 0)
                continue;

            var abbreviation = row.Abbreviation.ExtractText();
            if (string.IsNullOrEmpty(abbreviation))
                continue;

            result[row.RowId] = new JobInfo(
                row.RowId,
                abbreviation,
                row.Name.ExtractText(),
                RoleFrom(row.Role),
                JobIconBase + row.RowId);
        }

        return result;
    }

    /// <summary><c>ClassJob.Role</c>: 1 tank, 2 melee dps, 3 ranged dps, 4 healer.</summary>
    private static RaidRole RoleFrom(byte role) => role switch
    {
        1 => RaidRole.Tank,
        2 or 3 => RaidRole.Dps,
        4 => RaidRole.Healer,
        _ => RaidRole.Unknown,
    };
}
