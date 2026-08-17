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

    /// <summary>
    /// What the whole target set would do, against what they are wearing now. The finish line.
    ///
    /// Built by starting from the measured set and applying every slot's difference, rather than by
    /// adding a set up from parts. Two reasons, and the second is the important one:
    ///
    /// <list type="bullet">
    /// <item>Adding up from parts would need a character's base stats, their clan bonus and the food
    /// formula, none of which is needed anywhere else.</item>
    /// <item>It would be a <b>second way</b> of arriving at a number this plugin already computes,
    /// free to disagree with the per-slot gains sitting next to it on the same screen. Every slot's
    /// gain summed has to equal the set's gain, and the only way to guarantee that is to compute one
    /// from the other.</item>
    /// </list>
    ///
    /// It inherits the same stated assumption: melds carry over, so a target set with more meld slots
    /// than the current one is worth a little more than this says.
    /// </summary>
    public GearGain? TargetGain(RosterMember member)
    {
        if (builder.For(member) is not { } before || builder.LevelFor(before.Level) is not { } level)
            return null;

        var job = jobs.Get(member.MeasuredJobId);
        var profile = profiles.For(member.MeasuredJobId);

        var after = before;
        var changed = false;

        foreach (var slot in Slots.All)
        {
            var need = member.NeedFor(slot);

            if (need.BisItemId == 0 || need.BisItemId == need.EquippedItemId)
                continue;

            if (!items.TryGetStats(need.BisItemId, out var target))
                continue;

            var worn = need.EquippedItemId != 0 && items.TryGetStats(need.EquippedItemId, out var found)
                           ? StatsOf(found)
                           : [];

            after = GearDelta.Apply(after, job.PrimaryStat, GearDelta.Between(worn, StatsOf(target)),
                                    slot == GearSlot.Weapon ? target.WeaponDamage : 0,
                                    slot == GearSlot.Weapon ? target.DelayMs : 0);

            changed = true;
        }

        if (DamageModel.Estimate(before, profile, level) is not { } baseline)
            return null;

        // Already wearing the target set: the gain is zero rather than unknown, and the two estimates
        // being the same object says exactly that.
        if (!changed)
            return new GearGain(baseline, baseline);

        return DamageModel.Estimate(after, profile, level) is { } improved
                   ? new GearGain(baseline, improved)
                   : null;
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
