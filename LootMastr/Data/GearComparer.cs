using System.Collections.Generic;
using LootMastr.Planning.Dps;
using LootMastr.Roster;

namespace LootMastr.Data;

/// <summary>
/// What a player's set is worth, and what a different piece would do to it.
///
/// The one place the damage model meets the roster, so the awkward parts live here rather than being
/// spread over the UI: which stats an item actually contributes, whether the high quality bonus
/// counts, and what happens when a slot has never been read.
/// </summary>
public sealed class GearComparer
{
    private readonly ItemCatalog items;
    private readonly JobCatalog jobs;
    private readonly StatBlockBuilder builder;
    private readonly JobProfileCatalog profiles;

    public GearComparer(ItemCatalog items, JobCatalog jobs, StatBlockBuilder builder,
                        JobProfileCatalog profiles)
    {
        this.items = items;
        this.jobs = jobs;
        this.builder = builder;
        this.profiles = profiles;
    }

    /// <summary>What this player does on what they are wearing, or nothing if it cannot be read.</summary>
    public DamageEstimate? Estimate(RosterMember member)
    {
        if (builder.For(member) is not { } stats || builder.LevelFor(stats.Level) is not { } level)
            return null;

        return DamageModel.Estimate(stats, profiles.For(member.MeasuredJobId), level);
    }

    /// <summary>
    /// What putting <paramref name="itemId"/> in <paramref name="slot"/> would be worth.
    ///
    /// Null when there is nothing to compare against — no measured stats, no weapon, or an item the
    /// catalogue has no stat line for. A gain of zero and "cannot say" are different answers and the
    /// caller has to be able to tell them apart.
    /// </summary>
    public GearGain? Gain(RosterMember member, GearSlot slot, uint itemId)
    {
        if (itemId == 0)
            return null;

        if (builder.For(member) is not { } before || builder.LevelFor(before.Level) is not { } level)
            return null;

        if (!items.TryGetStats(itemId, out var candidate))
            return null;

        var job = jobs.Get(member.MeasuredJobId);
        var profile = profiles.For(member.MeasuredJobId);

        var worn = member.NeedFor(slot).EquippedItemId;
        var wornStats = worn != 0 && items.TryGetStats(worn, out var found) ? StatsOf(found) : [];

        var changes = GearDelta.Between(wornStats, StatsOf(candidate));

        // A weapon carries damage and delay, which are not stats and do not appear in that list.
        var weaponDamage = slot == GearSlot.Weapon ? candidate.WeaponDamage : 0;
        var delay = slot == GearSlot.Weapon ? candidate.DelayMs : 0;

        var after = GearDelta.Apply(before, job.PrimaryStat, changes, weaponDamage, delay);

        if (DamageModel.Estimate(before, profile, level) is not { } baseline ||
            DamageModel.Estimate(after, profile, level) is not { } improved)
        {
            return null;
        }

        return new GearGain(baseline, improved);
    }

    /// <summary>Convenience: what the target piece for a slot would be worth over what is worn.</summary>
    public GearGain? GainOfTarget(RosterMember member, GearSlot slot)
    {
        var need = member.NeedFor(slot);
        return need.BisItemId == 0 || need.BisItemId == need.EquippedItemId
                   ? null
                   : Gain(member, slot, need.BisItemId);
    }

    /// <summary>
    /// An item's stats as changes.
    ///
    /// The high quality bonus is included whenever the item can have one. That is an assumption, and
    /// it is the right one: nobody wears normal quality crafted gear in a savage set, and the probe
    /// confirmed <c>BaseParamSpecial</c> is the bonus on top rather than the total, so adding it is
    /// arithmetic rather than a guess about which of two numbers to use.
    /// </summary>
    private static List<StatChange> StatsOf(ItemStats stats)
    {
        var result = new List<StatChange>(stats.Params.Count);

        foreach (var stat in stats.Params)
            result.Add(new StatChange(stat.BaseParam, stat.Value));

        if (!stats.CanBeHq)
            return result;

        foreach (var bonus in stats.HqParams)
        {
            var index = result.FindIndex(s => s.BaseParam == bonus.BaseParam);

            if (index >= 0)
                result[index] = new StatChange(bonus.BaseParam, result[index].Delta + bonus.Value);
            else
                result.Add(new StatChange(bonus.BaseParam, bonus.Value));
        }

        return result;
    }
}
