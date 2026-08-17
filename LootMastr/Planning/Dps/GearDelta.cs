using System.Collections.Generic;

namespace LootMastr.Planning.Dps;

/// <summary>One stat moving by one amount, as a swapped item leaves it.</summary>
public readonly record struct StatChange(uint BaseParam, int Delta);

/// <summary>What one set is worth against another, and by how much.</summary>
public readonly record struct GearGain(DamageEstimate Before, DamageEstimate After)
{
    public double Dps => After.EstimatedDps - Before.EstimatedDps;

    /// <summary>
    /// The change as a percentage of what they do now, on DPS rather than on damage per 100 potency.
    ///
    /// Damage per 100 potency is the exact half and would be the tidier thing to quote, but it
    /// cannot see a speed stat at all — a piece that buys a shorter recast changes nothing in it.
    /// So the percentage runs on the number that accounts for everything, and the exact figures sit
    /// beside it for anyone who wants to check the part that is not modelled.
    /// </summary>
    public double Percent => Before.EstimatedDps <= 0 ? 0 : ((After.EstimatedDps / Before.EstimatedDps) - 1) * 100;

    public double PerHundredGain => After.DamagePer100Potency - Before.DamagePer100Potency;

    public bool IsUpgrade => Dps > 0;
}

/// <summary>
/// Moves a set from one piece of gear to another.
///
/// Both pieces arrive with their melds already folded into their stat lists, which is what makes the
/// arithmetic exact rather than approximate. The measured totals a comparison starts from contain
/// whatever is melded <i>now</i>, so a swap has to take those out again and put the new piece's in.
/// Nothing here needs to know that — it is the caller's job to hand over complete pieces — but it is
/// why the caller does.
/// </summary>
public static class GearDelta
{
    public static StatBlock Apply(
        StatBlock stats, uint primaryStat, IReadOnlyList<StatChange> changes,
        int weaponDamage = 0, int weaponDelayMs = 0)
    {
        var result = stats;

        foreach (var change in changes)
        {
            if (change.Delta != 0)
                result = result.With(change.BaseParam, change.Delta, primaryStat);
        }

        return weaponDamage > 0 ? result.WithWeapon(weaponDamage, weaponDelayMs) : result;
    }

    /// <summary>
    /// One stat list with another added on top of it — a piece plus its melds, or plus its high
    /// quality bonus.
    ///
    /// Adding rather than replacing, and that is the whole point: a critical hit materia in a piece
    /// that already has critical hit has to land on the same entry, or <see cref="Between"/> sees two
    /// entries for one stat and counts whichever it meets first.
    ///
    /// <b>Assumes no stat is melded past the item's cap.</b> The game reduces a materia that would
    /// overshoot, and the cap is not in any sheet column this plugin reads. A set built in a gear
    /// planner never overshoots, so for a target set this is exact; for a set somebody melded by hand
    /// it can read a point or two high.
    /// </summary>
    public static List<StatChange> Plus(IReadOnlyList<StatChange> stats, IReadOnlyList<StatChange> extra)
    {
        var result = new List<StatChange>(stats.Count + extra.Count);
        result.AddRange(stats);

        foreach (var stat in extra)
        {
            var index = result.FindIndex(s => s.BaseParam == stat.BaseParam);

            if (index >= 0)
                result[index] = new StatChange(stat.BaseParam, result[index].Delta + stat.Delta);
            else
                result.Add(stat);
        }

        return result;
    }

    /// <summary>
    /// The stat difference between two pieces, as changes to apply to a set wearing the old one.
    ///
    /// Both lists are short — six entries at most — so this is two nested loops rather than a
    /// dictionary, and the allocation saved matters more than the comparisons do.
    /// </summary>
    public static List<StatChange> Between(
        IReadOnlyList<StatChange> oldPiece, IReadOnlyList<StatChange> newPiece)
    {
        var result = new List<StatChange>(oldPiece.Count + newPiece.Count);

        foreach (var stat in newPiece)
            result.Add(new StatChange(stat.BaseParam, stat.Delta - ValueOf(oldPiece, stat.BaseParam)));

        // Anything the old piece had and the new one does not is a loss, and has to be counted or a
        // sidegrade reads as a pure gain.
        foreach (var stat in oldPiece)
        {
            if (ValueOf(newPiece, stat.BaseParam) == 0)
                result.Add(new StatChange(stat.BaseParam, -stat.Delta));
        }

        return result;
    }

    private static int ValueOf(IReadOnlyList<StatChange> stats, uint baseParam)
    {
        foreach (var stat in stats)
        {
            if (stat.BaseParam == baseParam)
                return stat.Delta;
        }

        return 0;
    }
}
