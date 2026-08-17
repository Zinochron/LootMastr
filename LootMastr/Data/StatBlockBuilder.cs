using System;
using LootMastr.Planning.Dps;
using LootMastr.Roster;
using Lumina.Excel.Sheets;

namespace LootMastr.Data;

/// <summary>
/// Turns a roster row into something the damage model can read.
///
/// Three sources, and which one each number comes from is the point:
///
/// <list type="bullet">
/// <item>The <b>substats</b> are measured — read off the character, materia and food already in
/// them. Nothing here adds anything up.</item>
/// <item>The <b>weapon</b> comes off the item, because the attribute table does not carry weapon
/// damage: a paladin holding a sword measures 0 for it. Exact anyway, since it cannot be melded.</item>
/// <item>The <b>level constants</b> come from <c>ParamGrow</c> for SUB and DIV, and from a table
/// for MAIN, which that sheet does not hold.</item>
/// </list>
///
/// Anything missing means no stat block, and no stat block means no estimate. Half of one would
/// rate somebody far below where they are with nothing on screen saying why.
/// </summary>
public sealed class StatBlockBuilder
{
    private readonly ItemCatalog items;
    private readonly JobCatalog jobs;

    public StatBlockBuilder(ItemCatalog items, JobCatalog jobs)
    {
        this.items = items;
        this.jobs = jobs;
    }

    /// <summary>The level constants as the game reports them, or null at a level with no MAIN.</summary>
    public LevelTable? LevelFor(int level)
    {
        if (level <= 0)
            return null;

        var sheet = Services.Data.GetExcelSheet<ParamGrow>();
        if (!sheet.TryGetRow((uint)level, out var row))
            return null;

        // BaseSpeed is SUB and LevelModifier is DIV. Not what the columns are called, and exactly
        // what they hold — 380/1300, 400/1900, 420/2780 at levels 80, 90 and 100.
        return LevelTable.For(level, row.BaseSpeed, row.LevelModifier);
    }

    public StatBlock? For(RosterMember member)
    {
        if (!member.HasMeasuredStats)
            return null;

        var job = jobs.Get(member.MeasuredJobId);
        if (!job.IsValid || job.PrimaryStat == 0 || job.PrimaryModifier <= 0)
            return null;

        var weaponId = member.NeedFor(GearSlot.Weapon).EquippedItemId;
        if (weaponId == 0 || !items.TryGetStats(weaponId, out var weapon))
            return null;

        var stats = new StatBlock(
            member.MeasuredLevel,
            job.PrimaryModifier,
            Value(member, job.PrimaryStat),
            weapon.WeaponDamage,
            weapon.DelayMs,
            Value(member, Attributes.CriticalHit),
            Value(member, Attributes.DirectHitRate),
            Value(member, Attributes.Determination),
            Value(member, Attributes.SkillSpeed),
            Value(member, Attributes.SpellSpeed),
            Value(member, Attributes.Tenacity));

        return stats.IsUsable ? stats : null;
    }

    private static int Value(RosterMember member, uint baseParam) =>
        member.Attributes.TryGetValue(baseParam, out var value) ? value : 0;
}
