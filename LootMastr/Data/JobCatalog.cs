using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace LootMastr.Data;

public readonly record struct JobInfo(
    uint Id, string Abbreviation, string Name, RaidRole Role, uint IconId,
    uint PrimaryStat, int PrimaryModifier, byte GameRole)
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
            : new JobInfo(0, "???", "Unknown", RaidRole.Unknown, 0, 0, 0, 0);

    public RaidRole RoleOf(uint jobId) => Get(jobId).Role;

    /// <summary>
    /// Whether this job is a physical ranged one — bard, machinist or dancer.
    ///
    /// <see cref="RaidRole"/> cannot say: it folds melee and ranged into one "dps", which is right for
    /// a loot queue and wrong for a damage trait. Physical ranged turned out to be the one category
    /// with a trait of its own, and telling it apart needs the game's finer role plus the primary
    /// stat — role 3 is ranged, and dexterity separates a dancer from a black mage.
    /// </summary>
    public bool IsPhysicalRanged(uint jobId)
    {
        var job = Get(jobId);
        return job.GameRole == 3 && job.PrimaryStat == 2;
    }

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

            // PrimaryStat is a BaseParam row id — paladin 1, ninja 2, black mage 4, sage 5 — which
            // the stat probe confirmed. The matching modifier is what the weapon damage term needs.
            result[row.RowId] = new JobInfo(
                row.RowId,
                abbreviation,
                row.Name.ExtractText(),
                RoleFrom(row.Role),
                JobIconBase + row.RowId,
                row.PrimaryStat,
                ModifierFor(row, row.PrimaryStat),
                row.Role);
        }

        return result;
    }

    /// <summary>The job modifier for its own primary stat, as a percentage of the level's MAIN.</summary>
    private static int ModifierFor(ClassJob row, uint primaryStat) => primaryStat switch
    {
        1 => row.ModifierStrength,
        2 => row.ModifierDexterity,
        4 => row.ModifierIntelligence,
        5 => row.ModifierMind,
        _ => 0,
    };

    /// <summary><c>ClassJob.Role</c>: 1 tank, 2 melee dps, 3 ranged dps, 4 healer.</summary>
    private static RaidRole RoleFrom(byte role) => role switch
    {
        1 => RaidRole.Tank,
        2 or 3 => RaidRole.Dps,
        4 => RaidRole.Healer,
        _ => RaidRole.Unknown,
    };
}
