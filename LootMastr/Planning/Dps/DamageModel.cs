using System;

namespace LootMastr.Planning.Dps;

/// <summary>What a set is worth, and how sure of it to be.</summary>
public readonly record struct DamageEstimate(
    double DamagePer100Potency,
    double Gcd,
    double EstimatedDps,
    string? Caveat)
{
    public bool IsEstimated => Caveat != null;
}

/// <summary>
/// Damage per 100 potency from stats, and an estimated DPS from that.
///
/// The first number is arithmetic the game itself does, and it is the one every comparison rests
/// on: swapping a ring changes it exactly, and the ranking between two candidates for the same
/// coffer follows from it alone. The second multiplies it by how much potency a job lands per
/// second, which is a modelled quantity — see <see cref="JobProfile"/>.
///
/// So: <b>damage per 100 potency decides, estimated DPS explains.</b> The UI shows the DPS because
/// nobody thinks in potency, and puts the exact number in the tooltip beside it.
///
/// The flooring is not decoration. Every term in the game's formula truncates at a fixed number of
/// decimal places, which is why a stat can gain thirty points and change nothing at all — the
/// substat tiers people talk about are this. Rounding instead would make every tier boundary
/// invisible and every comparison slightly wrong in a way that never shows up as an error.
/// </summary>
public static class DamageModel
{
    /// <summary>The game's own recast before any speed: 2.50 seconds.</summary>
    private const int BaseRecastMs = 2500;

    public static DamageEstimate? Estimate(StatBlock stats, JobProfile profile, LevelTable level)
    {
        if (!stats.IsUsable)
            return null;

        var speed = stats.SpeedFor(profile.UsesSpellSpeed);

        var weaponDamage = Math.Floor((level.Main * stats.JobModifier / 1000.0) + stats.WeaponDamage);
        var attack = Math.Floor((profile.AttackPowerMultiplier * (stats.MainStat - level.Main) / (double)level.Main) + 100) / 100.0;
        var determination = Math.Floor((140.0 * (stats.Determination - level.Main) / level.Div) + 1000) / 1000.0;

        // Tenacity only exists on a tank's gear; on anyone else the stat sits at its base and the
        // term would quietly shave a fraction of a percent off for no reason.
        var tenacity = profile.UsesTenacity
                           ? Math.Floor((112.0 * (stats.Tenacity - level.Sub) / level.Div) + 1000) / 1000.0
                           : 1.0;

        var critRate = Math.Floor((200.0 * (stats.CriticalHit - level.Sub) / level.Div) + 50) / 1000.0;
        var critDamage = Math.Floor((200.0 * (stats.CriticalHit - level.Sub) / level.Div) + 1400) / 1000.0;
        var directRate = Math.Floor(550.0 * (stats.DirectHit - level.Sub) / level.Div) / 1000.0;

        // Potency enters as a percentage, not a count: the product below already is the damage of a
        // 100 potency action. Multiplying by 100 on top of that is a factor of a hundred, and it
        // showed up as a paladin doing 1.2 million damage per second — which is the useful thing
        // about carrying an absolute number rather than only a ranking. A wrong ranking looks
        // plausible; a wrong number does not.
        var perHundred = weaponDamage
                       * attack
                       * determination
                       * tenacity
                       * profile.Trait
                       * (1 + (critRate * (critDamage - 1)))
                       * (1 + (directRate * 0.25));

        var gcd = Recast(speed, level);

        return new DamageEstimate(
            perHundred,
            gcd,
            perHundred / 100.0 * profile.PotencyPerSecondAt(gcd),
            profile.IsDefaulted
                ? $"No simulated potency figure for {profile.Abbreviation} yet — the damage per 100 " +
                  "potency is exact, the dps is an estimate."
                : null);
    }

    /// <summary>
    /// The global cooldown at a given speed stat, in seconds.
    ///
    /// Three truncations, in the game's order: speed becomes a reduction in thousandths, that scales
    /// the base recast to whole milliseconds, and the result lands on a hundredth of a second — which
    /// is why a recast reads 2.37 and never 2.3746.
    ///
    /// The anchor is that a character carrying no speed at all sits at exactly 2.50, and the stat
    /// probe confirmed where that is: direct hit, skill speed and spell speed all measured 420 on a
    /// set with none of them, which is SUB. The first version of this had the reduction subtracted
    /// from 2000 rather than 1000 and produced a five second recast — wrong by exactly double, and
    /// the reason a harness gets written before a UI does.
    /// </summary>
    public static double Recast(int speed, LevelTable level)
    {
        var reduction = Math.Floor(130.0 * (speed - level.Sub) / level.Div);
        var milliseconds = Math.Floor((1000 - reduction) * BaseRecastMs / 1000.0);

        return Math.Floor(milliseconds / 10.0) / 100.0;
    }
}
