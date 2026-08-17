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
/// <b>Only the items' own stats change hands.</b> Materia is left out of the difference entirely,
/// and that is a decision rather than an omission: the current set's melds are already inside the
/// measured totals this starts from, and what would be melded into a piece nobody owns yet is not
/// knowable. Leaving them out is exactly the assumption that the melds carry over, which is the
/// closest thing to true and errs low on a new piece with more meld slots than the old one.
///
/// The UI says so. An estimate whose one assumption is written on the screen is a different thing
/// from one that has quietly made it.
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
