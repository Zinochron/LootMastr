using System;

namespace LootMastr.Planning.Dps;

/// <summary>
/// The part of a job that the game's sheets do not say: roughly how much potency it puts out, and
/// how that potency is split between the global cooldown and everything else.
///
/// This is what turns "damage per 100 potency" — which is exact arithmetic — into an estimated DPS,
/// which is not. It is written down as data rather than derived, because deriving it means
/// simulating a rotation, and a rotation per job is a maintenance burden the size of the rest of
/// this plugin.
///
/// **What it does and does not affect.** The profile scales one job's number up or down as a whole.
/// It therefore moves the estimated DPS a player is shown, and it does <i>not</i> move the ranking
/// between two players of the same job competing for the same coffer — which is what the loot plan
/// actually asks. A profile that is ten percent out is a cosmetic problem, not a distribution one.
/// </summary>
public sealed record JobProfile(
    string Abbreviation,
    double PotencyPerGcd,
    double OgcdPotencyPerSecond,
    double AutoAttackShare,
    bool UsesSpellSpeed,
    bool UsesTenacity,
    double Trait,
    double AttackPowerMultiplier)
{
    /// <summary>
    /// What an unknown job gets: a middling profile, and a caveat saying so.
    ///
    /// Refusing outright would be worse. A job with no entry still has exact stats and an exact
    /// damage per 100 potency, and the loot plan compares it against players of the same job — so
    /// the missing piece is the one that matters least.
    /// </summary>
    public static JobProfile Default(string abbreviation, bool magical, bool tank) =>
        new(abbreviation,
            PotencyPerGcd: 320,
            OgcdPotencyPerSecond: 40,
            AutoAttackShare: magical ? 0 : 0.08,
            UsesSpellSpeed: magical,
            UsesTenacity: tank,
            Trait: TraitFor(magical),
            AttackPowerMultiplier: tank ? 190 : 237);

    /// <summary>
    /// The job's damage trait: a flat multiplier on everything it does, and the single easiest term to
    /// forget. Leaving it at 1.0 for every job made every magical number 23% low, which looks like a
    /// plausible number and is not.
    ///
    /// Both values are now measured against Etro rather than assumed, and the second one corrected
    /// the first attempt:
    ///
    /// <list type="bullet">
    /// <item><b>Magical: 1.30.</b> Exactly the factor between this formula and Etro's figure for a
    /// black mage set.</item>
    /// <item><b>Physical: 1.00.</b> A paladin set came out at exactly 1.00000. The conventional
    /// wisdom of a 1.20 "Maim and Mastery" was wrong here, and would have overstated every physical
    /// job by a fifth.</item>
    /// </list>
    ///
    /// The two are not as asymmetric as they look: physical jobs already scale differently through
    /// <see cref="AttackPowerMultiplier"/>, which is 190 for a tank against 237 for a caster. What
    /// remains unverified is whether a <i>melee or ranged physical</i> job sits at 1.00 like the tank
    /// or carries a trait of its own — that wants a third set to settle, and 1.00 is now the reading
    /// with evidence behind it rather than the one without.
    /// </summary>
    public static double TraitFor(bool magical) => magical ? 1.30 : 1.00;

    public bool IsDefaulted { get; init; } = true;

    /// <summary>Potency landed per second at this recast, before the crit and direct hit terms.</summary>
    public double PotencyPerSecond(double gcd)
    {
        if (gcd <= 0)
            return 0;

        return ((PotencyPerGcd / gcd) + OgcdPotencyPerSecond) * (1 + Math.Max(0, AutoAttackShare));
    }
}
